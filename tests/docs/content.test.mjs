import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { load } from "cheerio";
import { enhanceContent, headingIds, readLocales, readSource, pageOrder } from "../../scripts/docs-content.mjs";

test("every declared language has the complete navigation and UI contract", () => {
  const locales = readLocales();
  assert(locales.en && locales.fr);
  for (const locale of Object.values(locales)) {
    assert.equal(Object.keys(locale.pages).length, pageOrder.length);
    for (const value of Object.values(locale.ui)) assert(value);
  }
});

test("section anchors preserve existing IDs and disambiguate repeated headings", () => {
  const $ = load('<main><h2 id="kept">One</h2><h2>Déjà vu</h2><h2>Déjà vu</h2><h3 id="deja-vu">Nested</h3></main>');
  assert.deepEqual(headingIds($), ["kept", "deja-vu-2", "deja-vu-3", "deja-vu"]);
});

test("translated pages use their English section identity and preserve code", () => {
  const $ = load('<main><h1>Test</h1><h2>Préparation</h2><pre><code>const x = "&lt;value&gt;";\n</code></pre><table><thead><tr><th>Nom</th></tr></thead></table></main>');
  const ui = readLocales().fr.ui;
  const result = enhanceContent($, ["preparation"], ui, "../");
  assert.equal(result.toc[0].id, "preparation");
  assert.equal(result.toc[0].text, "Préparation");
  assert.equal($("pre code").text(), 'const x = "<value>";\n');
  assert.equal($(".copy-button").text(), "Copier");
  assert.equal($("th").attr("scope"), "col");
  assert.equal($(".table-scroll").attr("tabindex"), "0");
});

test("incomplete translations fail rather than silently dropping sections", () => {
  assert.throws(() => enhanceContent(load("<main><h2>Un</h2></main>"), ["one", "two"], readLocales().fr.ui, "../"), /preserve all section/);
});

test("linked card headings do not acquire nested anchors or clutter the contents", () => {
  const $ = load('<main><h2>Next steps</h2><a class="card" href="tools.html"><h3>Tools</h3><p>Reference.</p></a></main>');
  const result = enhanceContent($, ["next-steps", "tools"], readLocales().en.ui, "");
  assert.equal($("a.card a").length, 0);
  assert.equal($("a.card h3").attr("id"), "tools");
  assert.deepEqual(result.toc.map(item => item.id), ["next-steps"]);
});

test("labelled code and table containers are enhanced rather than nested", () => {
  const $ = load('<main><nav class="toc"><a href="#setup">Setup</a></nav><section id="setup"><h2>Setup</h2><div class="code-block"><div class="code-heading">C# example</div><pre><code>await RunAsync();</code></pre></div><div class="table-wrap"><table><tr><td>Value</td></tr></table></div></section></main>');
  const ids = headingIds($);
  enhanceContent($, ids, readLocales().en.ui, "");
  assert.equal($(".code-block").length, 1);
  assert.equal($(".code-heading .copy-button").length, 1);
  assert.equal($(".table-wrap.table-scroll").length, 1);
  assert.equal($("main > nav.toc").length, 0);
  assert.equal($("section").attr("id"), "setup");
  assert.notEqual($("h2").attr("id"), "setup");
});

test("nested source navigation and illustrated homepage content remain intact", () => {
  const $ = load('<main><div class="hero"><h1>A squad.<br>Your app.</h1><img class="hero-logo" src="assets/logo.svg" alt="Three nodes"></div><section id="publishing"><h2>Publishing</h2><nav class="toc"><p>Publication sequence</p><a href="#publish">Publish</a></nav></section></main>');
  enhanceContent($, headingIds($), readLocales().en.ui, "../");
  assert.equal($("h1").html(), "A squad.<br>Your app.");
  assert.equal($("img").attr("src"), "../assets/logo.svg");
  assert.equal($("img").attr("alt"), "Three nodes");
  assert.equal($(".source-toc").text(), "Publication sequencePublish");
});

test("both languages describe the actual package versions and release-pinned public examples", () => {
  const project = readFileSync(new URL("../../src/HveSquad.AgentFramework/HveSquad.AgentFramework.csproj", import.meta.url), "utf8");
  const maf = project.match(/Include="Microsoft.Agents.AI" Version="([^"]+)"/)[1];
  const extensions = project.match(/Include="Microsoft.Extensions.AI" Version="([^"]+)"/)[1];
  const manifest = JSON.parse(readFileSync(new URL("../../compatibility/hve-squad-release.json", import.meta.url), "utf8"));
  for (const prefix of ["", "fr/"]) {
    for (const slug of pageOrder) {
      const { $ } = readSource(slug, prefix);
      assert($(".sidebar-bottom").text().includes(`MAF ${maf.split(".").slice(0, 2).join(".")}`));
      assert(!$("main").text().includes("10.8.3"));
      assert(!$("main").text().includes("1.17.0"));
    }
    const setup = readSource("getting-started", prefix).$;
    assert(setup("main").text().includes(`Microsoft.Extensions.AI ${extensions}`));
    const examples = readSource("use-cases", prefix).$;
    assert(examples("pre").text().includes(`Version = "${manifest.tag}"`));
    assert(setup("#package-verification").length === 1);
    assert(setup("pre").text().includes("scripts\\Test-PackageConsumer.ps1"));
  }
});

test("native helper and reviewed-upgrade commands keep their distinct permission boundaries", () => {
  for (const prefix of ["", "fr/"]) {
    const runtime = readSource("usage", prefix).$;
    const commands = runtime("#helper-cli pre").text();
    for (const flag of ["--memory remember", "--memory read", "--learn discover", "--learn prepare", "--learn promote"]) {
      assert(commands.includes(flag), `Missing real helper command ${flag}`);
    }
    const selection = runtime("tr").filter((_, element) =>
      runtime(element).find("td:first-child code").text() === "SelectCouncilAsync");
    assert.equal(selection.length, 1);
    assert.equal(selection.closest("table").find("caption").text(), prefix
      ? "Options réservées à l’hôte : à configurer dans le code, pas en JSON"
      : "Host-only options: configure in code, not JSON");
    const maintenance = readSource("maintaining", prefix).$;
    assert(maintenance("#reviewed-alignment").length === 1);
    for (const workflow of ["release-alignment.yml", "release-candidate.yml"]) {
      assert(maintenance("#workflows").text().includes(workflow));
    }
    assert(maintenance("#updates").text().includes("candidate-linux"));
    assert(maintenance("#updates").text().includes("candidate-windows"));
    assert(maintenance("#updates").text().includes("unbaselined"));
    assert(maintenance("#updates").text().includes("not judged"));
  }
});

test("individual conformance examples require full tests and actual package evidence in both languages", () => {
  for (const prefix of ["", "fr/"]) {
    const $ = readSource("maintaining", prefix).$;
    assert.equal($("#native-conformance").length, 1);
    const commands = $("#native-conformance").nextAll(".code-block").first().find("pre").text();
    for (const required of ["HVE_SQUAD_REQUIRE_RELEASE_TESTS", "Test-PackageConsumer.ps1", "verify-conformance", "--package-proof", "candidate.trx"]) {
      assert(commands.includes(required), `Missing actual acceptance input ${required}`);
    }
    assert(!commands.includes("--filter"));
    assert($("main").text().includes("IndustryIQ"));
    assert($("a[href$='compatibility/hve-squad-conformance.json']").length > 0);
  }
});
