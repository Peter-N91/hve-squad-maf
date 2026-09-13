(() => {
  "use strict";
  const root = document.documentElement;
  const queryTheme = new URLSearchParams(window.location.search).get("scoutTheme");
  if (queryTheme === "light" || queryTheme === "dark") {
    root.setAttribute("data-theme", queryTheme);
  }
  const themeButton = document.querySelector(".theme-toggle");
  if (themeButton) {
    themeButton.hidden = false;
    const updateLabel = () => {
      const dark = root.getAttribute("data-theme") === "dark";
      themeButton.textContent = dark ? "Light theme" : "Dark theme";
      themeButton.setAttribute("aria-label", `Switch to ${dark ? "light" : "dark"} theme`);
    };
    updateLabel();
    themeButton.addEventListener("click", () => {
      root.setAttribute("data-theme", root.getAttribute("data-theme") === "dark" ? "light" : "dark");
      updateLabel();
    });
  }
  const menuButton = document.querySelector(".mobile-toggle");
  const navigation = document.getElementById("site-navigation");
  if (menuButton && navigation) {
    const mobile = window.matchMedia("(max-width: 800px)");
    const syncMenu = () => {
      menuButton.hidden = !mobile.matches;
      navigation.hidden = mobile.matches;
      menuButton.setAttribute("aria-expanded", String(!navigation.hidden));
    };
    syncMenu();
    mobile.addEventListener("change", syncMenu);
    menuButton.addEventListener("click", () => {
      navigation.hidden = !navigation.hidden;
      menuButton.setAttribute("aria-expanded", String(!navigation.hidden));
    });
    document.addEventListener("keydown", (event) => {
      if (event.key === "Escape" && mobile.matches && !navigation.hidden) {
        navigation.hidden = true;
        menuButton.setAttribute("aria-expanded", "false");
        menuButton.focus();
      }
    });
  }
  if (navigator.clipboard && window.isSecureContext) {
    document.querySelectorAll(".code-block").forEach((block) => {
      const heading = block.querySelector(".code-heading");
      const code = block.querySelector("pre code");
      if (!heading || !code) return;
      const button = document.createElement("button");
      button.type = "button";
      button.className = "copy-button";
      button.textContent = "Copy";
      button.setAttribute("aria-label", `Copy ${heading.textContent.trim()}`);
      button.addEventListener("click", async () => {
        try {
          await navigator.clipboard.writeText(code.textContent);
          button.textContent = "Copied";
          document.getElementById("copy-status").textContent = "Code copied to clipboard.";
        } catch {
          button.textContent = "Select code";
          document.getElementById("copy-status").textContent = "Copy unavailable. Select and copy the code manually.";
        }
        window.setTimeout(() => { button.textContent = "Copy"; }, 2500);
      });
      heading.append(button);
    });
  }
})();
