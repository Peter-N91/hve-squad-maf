using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace HveSquad.AgentFramework.Runtime;

internal sealed class RuntimeStopException(
    SquadRunStatus status, string message, SquadEvidenceFailureClass failureClass = SquadEvidenceFailureClass.None) : Exception(message)
{
    internal SquadRunStatus Status { get; } = status;
    internal SquadEvidenceFailureClass FailureClass { get; } = failureClass;
}

internal sealed class RuntimeGuard
{
    private static readonly Action<ILogger, Exception?> LogToolFailure = LoggerMessage.Define(
        LogLevel.Error, new EventId(1, "ToolFailure"), "Registered tool execution failed.");
    private readonly SquadRuntimeOptions _options;
    private readonly Action<RunBudget>? _persistBudget;
    private int _dispatches;
    private int _modelCalls;
    private int _toolCalls;
    private RuntimeStopException? _stop;

    internal RuntimeGuard(SquadRuntimeOptions options, RunBudget? budget = null, Action<RunBudget>? persistBudget = null)
    {
        _options = options;
        _dispatches = budget?.Dispatches ?? 0;
        _modelCalls = budget?.ModelCalls ?? 0;
        _toolCalls = budget?.ToolCalls ?? 0;
        _persistBudget = persistBudget;
    }

    internal void Check(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stop is { } stop)
        {
            throw stop;
        }
    }

    internal RuntimeStopException Stop(
        SquadRunStatus status, string message, SquadEvidenceFailureClass failureClass = SquadEvidenceFailureClass.None)
    {
        var stop = new RuntimeStopException(status, message, failureClass);
        return Interlocked.CompareExchange(ref _stop, stop, null) ?? stop;
    }

    internal void ClearConfirmedCouncilDecline()
    {
        var stop = _stop;
        if (stop?.Status != SquadRunStatus.ApprovalDenied)
        {
            throw new InvalidOperationException("Only a confirmed human council decline can be converted to a scoped waiver.");
        }
        _ = Interlocked.CompareExchange(ref _stop, null, stop);
    }

    internal RuntimeStopException Fail(string toolName, Exception exception)
    {
        if (_options.LoggerFactory is { } factory)
        {
            LogToolFailure(factory.CreateLogger("HveSquad.AgentFramework.Runtime.Tools"), exception);
        }
        return Stop(exception is RuntimeHistoryIntegrityException integrity ? integrity.Status : SquadRunStatus.Failed,
            $"Tool '{toolName}' failed: {exception.Message}",
            exception is RuntimeHistoryIntegrityException ? SquadEvidenceFailureClass.HistoryIntegrity : SquadEvidenceFailureClass.None);
    }

    internal void Dispatch(int depth)
    {
        var count = Interlocked.Increment(ref _dispatches);
        PersistBudget();
        if (depth > _options.MaxDepth || count > _options.MaxDispatches)
        {
            throw Stop(SquadRunStatus.LimitReached, "Dispatch count or nesting depth limit reached.");
        }
    }

    internal void Model(CancellationToken token)
    {
        Check(token);
        var count = Interlocked.Increment(ref _modelCalls);
        PersistBudget();
        if (count > _options.MaxModelCalls)
        {
            throw Stop(SquadRunStatus.LimitReached, "Model call limit reached.");
        }
    }

    internal void Tool(CancellationToken token)
    {
        Check(token);
        var count = Interlocked.Increment(ref _toolCalls);
        PersistBudget();
        if (count > _options.MaxToolCalls)
        {
            throw Stop(SquadRunStatus.LimitReached, "Tool call limit reached.");
        }
    }

    private void PersistBudget() => _persistBudget?.Invoke(new RunBudget
    {
        Dispatches = Volatile.Read(ref _dispatches),
        ModelCalls = Volatile.Read(ref _modelCalls),
        ToolCalls = Volatile.Read(ref _toolCalls),
    });
}

internal sealed class RuntimeFunction(
    AIFunction function,
    RuntimeGuard guard,
    Func<AIFunctionArguments, CancellationToken, ValueTask>? before = null,
    Action<AIFunctionArguments, object?>? after = null,
    Func<AIFunctionArguments, (bool Found, object? Result)>? replay = null,
    SemaphoreSlim? invocationLock = null) : DelegatingAIFunction(function)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            if (invocationLock is not null)
            {
                await invocationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
            }
            guard.Tool(cancellationToken);
            if (replay?.Invoke(arguments) is { Found: true } saved)
            {
                return saved.Result;
            }

            if (before is not null)
            {
                await before(arguments, cancellationToken).ConfigureAwait(false);
            }

            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            after?.Invoke(arguments, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RuntimeStopException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw guard.Fail(Name, ex);
        }
        finally
        {
            if (acquired)
            {
                invocationLock!.Release();
            }
        }
    }
}

internal sealed class RuntimeChatClient(
    IChatClient inner, RuntimeGuard guard, SemaphoreSlim? modelSlots = null,
    ModelCallObservation? context = null, Action<ModelCallObservation>? record = null,
    RuntimeModelRoute? initialRoute = null,
    Func<RuntimeModelRoute, SquadModelUnavailableException, IReadOnlySet<string>, CancellationToken, ValueTask<RuntimeModelRoute>>? reselect = null,
    Action<RuntimeModelRoute, string?>? validateObserved = null,
    Func<ChatResponse, SquadTokenUsage?>? usageSelector = null,
    Func<RuntimeModelRoute?, long, CancellationToken, ValueTask>? beforeCall = null,
    bool discoveryInterview = false,
    Func<CancellationToken, ValueTask>? providerReady = null,
    IReadOnlyList<SquadSkillScriptBinding>? skillScripts = null,
    TimeProvider? timeProvider = null) : DelegatingChatClient(inner)
{
    private int _callOrdinal;
    internal RuntimeModelRoute? Route { get; private set; } = initialRoute;
    internal string? ObservedModel { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (modelSlots is not null)
        {
            await modelSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var attempted = new HashSet<string>(StringComparer.Ordinal);
            var snapshot = messages.ToArray();
            while (true)
            {
                var contextTokens = ((long)System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(snapshot)) +
                    System.Text.Encoding.UTF8.GetByteCount(options?.Instructions ?? "") + 3L) / 4;
                if (beforeCall is not null)
                {
                    await beforeCall(Route, contextTokens, cancellationToken).ConfigureAwait(false);
                }
                guard.Model(cancellationToken);
                var callOptions = options?.Clone() ?? new();
                if (Route is { } route)
                {
                    callOptions.ModelId = route.ParameterModel;
                }
                if (callOptions.ModelId is { } id)
                {
                    attempted.Add(id);
                }
                if (callOptions.Tools is not null && skillScripts is not { Count: > 0 })
                {
                    callOptions.Tools = callOptions.Tools.Where(t => t.Name != AgentSkillsProvider.RunSkillScriptToolName).ToList();
                }
                var toolsExecutedByProvider = 0;
                var questionsAskedByProvider = 0;
                if (callOptions.Tools is not null)
                {
                    callOptions.Tools = callOptions.Tools.Select(tool => tool is AIFunction function
                        ? new ProviderInvocationFunction(function, arguments =>
                        {
                            Interlocked.Increment(ref toolsExecutedByProvider);
                            if (function.Name == AgentSkillsProvider.RunSkillScriptToolName)
                            {
                                SkillScriptContracts.ValidateCall(skillScripts ?? [], arguments, guard);
                            }
                            if (discoveryInterview && function.Name == "ask_host" &&
                                Interlocked.Increment(ref questionsAskedByProvider) > 1)
                            {
                                throw guard.Stop(SquadRunStatus.Blocked,
                                    "Discovery interviews ask exactly one human question per provider call; no second question was sent.");
                            }
                        })
                        : tool).ToList();
                }
                var observation = context is null ? null : context with
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CallOrdinal = Interlocked.Increment(ref _callOrdinal),
                    RequestedModel = Route?.PolicyApplied == true ? Route.RequestedModel : callOptions.ModelId,
                    EffectiveModel = callOptions.ModelId,
                    RouteRationale = Route?.PolicyApplied == true ? Route.Rationale : null,
                    AssignmentClass = Route?.PolicyApplied == true ? Route.AssignmentClass : null,
                    Floor = Route?.PolicyApplied == true ? Route.Floor : null,
                    EstimatedContextTokens = contextTokens,
                    RecordedAt = (timeProvider ?? TimeProvider.System).GetUtcNow(),
                };
                if (observation is not null)
                {
                    record?.Invoke(observation);
                }
                var settled = false;
                try
                {
                    if (providerReady is not null)
                    {
                        await providerReady(cancellationToken).ConfigureAwait(false);
                    }
                    var response = Route is { } activeRoute
                        ? await activeRoute.Client.GetResponseAsync(snapshot, callOptions, cancellationToken).ConfigureAwait(false)
                        : await base.GetResponseAsync(snapshot, callOptions, cancellationToken).ConfigureAwait(false);
                    var observed = Route?.PolicyApplied == true &&
                        (response.ModelId is null or "" or "unknown" or "unreported" or "unverified")
                        ? null : response.ModelId;
                    if (observation is not null)
                    {
                        var usage = usageSelector?.Invoke(response);
                        if (response.Usage?.InputTokenCount is < 0 || response.Usage?.OutputTokenCount is < 0 ||
                            usage is not null && (usage.InputTokens < 0 || usage.CachedTokens < 0 ||
                            usage.CachedTokens > usage.InputTokens || usage.CacheWriteTokens < 0 ||
                            usage.CacheWriteTokens > usage.InputTokens || usage.OutputTokens < 0 ||
                            response.Usage?.InputTokenCount is { } reportedInput && reportedInput != usage.InputTokens ||
                            response.Usage?.OutputTokenCount is { } reportedOutput && reportedOutput != usage.OutputTokens))
                        {
                            if (response.Usage is { InputTokenCount: >= 0, OutputTokenCount: >= 0 } actualUsage)
                            {
                                record?.Invoke(observation with
                                {
                                    Status = "returned",
                                    ObservedModel = observed,
                                    InputTokens = actualUsage.InputTokenCount,
                                    OutputTokens = actualUsage.OutputTokenCount,
                                    RouteRationale = Route?.PolicyApplied == true ? Route.IdentityRationale(observed) : null,
                                });
                                settled = true;
                            }
                            throw guard.Stop(SquadRunStatus.Blocked, "The trusted token normalization disagrees with provider counters or has invalid token counts.");
                        }
                        record?.Invoke(observation with
                        {
                            Status = "returned",
                            ObservedModel = observed,
                            InputTokens = usage?.InputTokens ?? response.Usage?.InputTokenCount,
                            OutputTokens = usage?.OutputTokens ?? response.Usage?.OutputTokenCount,
                            CachedInputTokens = usage?.CachedTokens,
                            CacheWriteTokens = usage?.CacheWriteTokens,
                            RouteRationale = Route?.PolicyApplied == true ? Route.IdentityRationale(observed) : null,
                        });
                    }
                    settled = true;
                    guard.Check(cancellationToken);
                    if (Route is { } active)
                    {
                        validateObserved?.Invoke(active, observed);
                    }
                    if (discoveryInterview &&
                        response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
                            .Count(call => call.Name == "ask_host") > 1)
                    {
                        throw guard.Stop(SquadRunStatus.Blocked,
                            "Discovery interviews ask exactly one human question per provider response; batched questionnaires are forbidden.");
                    }
                    foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
                    {
                        if (call.Name == AgentSkillsProvider.RunSkillScriptToolName)
                        {
                            if (skillScripts is not { Count: > 0 })
                            {
                                throw guard.Stop(SquadRunStatus.Unsupported, "No registered trusted skill script is available for this agent.");
                            }
                            SkillScriptContracts.ValidateCall(skillScripts, call.Arguments ?? new Dictionary<string, object?>(), guard);
                        }
                        if (callOptions.Tools?.Any(t => t.Name == call.Name) != true)
                        {
                            throw guard.Stop(SquadRunStatus.Blocked, $"Function '{call.Name}' is not registered for this agent.");
                        }
                        if (call.Name is AgentSkillsProvider.LoadSkillToolName or AgentSkillsProvider.ReadSkillResourceToolName)
                        {
                            guard.Tool(cancellationToken);
                        }
                    }
                    ObservedModel = observed ?? ObservedModel;
                    return response;
                }
                catch (SquadModelUnavailableException ex) when (Route is not null && reselect is not null)
                {
                    if (Volatile.Read(ref toolsExecutedByProvider) != 0)
                    {
                        throw guard.Stop(SquadRunStatus.Blocked,
                            "The host's model rejection followed actual tool execution and was not pre-execution. No model or effect was retried; inspect the recorded effects and partial outputs.");
                    }
                    settled = true;
                    if (observation is not null)
                    {
                        record?.Invoke(observation with { Status = "rejected" });
                    }
                    Route = await reselect(Route, ex, attempted, cancellationToken).ConfigureAwait(false);
                    guard.Check(cancellationToken);
                }
                finally
                {
                    if (observation is not null && !settled)
                    {
                        record?.Invoke(observation with { Status = cancellationToken.IsCancellationRequested ? "cancelled" : "failed" });
                    }

                }
            }
        }
        finally
        {
            if (modelSlots is not null)
            {
                modelSlots.Release();
            }
        }
    }

    private sealed class ProviderInvocationFunction(AIFunction function, Action<AIFunctionArguments> invoked) : DelegatingAIFunction(function)
    {
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            invoked(arguments);
            return base.InvokeCoreAsync(arguments, cancellationToken);
        }
    }
}
