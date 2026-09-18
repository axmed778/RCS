// Vendored progressive enhancement only: no framework, no build step, no network access (ADR-003).
// Everything here is a convenience; every page works with JavaScript disabled.
(function () {
    "use strict";

    // Registering a response: the conclusiveness default follows the response type, and a conditional outcome is
    // never conclusive by default (WORKFLOW.md §4.2). The clerk always decides; this only pre-fills the box.
    var type = document.querySelector("[data-response-type]");
    var outcome = document.querySelector("[data-response-outcome]");
    var conclusive = document.querySelector("[data-response-conclusive]");
    if (type && outcome && conclusive) {
        var suggest = function () {
            var option = type.options[type.selectedIndex];
            var defaultConclusive = option && option.getAttribute("data-conclusive") === "true";
            if (outcome.value === "CONDITIONAL") {
                defaultConclusive = false;
            }
            conclusive.checked = defaultConclusive;
        };
        type.addEventListener("change", suggest);
        outcome.addEventListener("change", suggest);
    }

    // Double submission is already safe (every create carries an operation id, ADR-020); this just makes it visible.
    Array.prototype.forEach.call(document.querySelectorAll("form[data-single-submit]"), function (form) {
        form.addEventListener("submit", function () {
            Array.prototype.forEach.call(form.querySelectorAll("button[type=submit]"), function (button) {
                button.disabled = true;
            });
        });
    });
})();
