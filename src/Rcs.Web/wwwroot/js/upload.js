// Progressive enhancement of the upload form: drag and drop, a client-side size check, and a progress bar.
// Without script the same form posts normally and the server applies every rule itself (the 500 MB limit included).
// A resubmission after a lost response is harmless: the form carries one operation id for all its attempts (ADR-020).
(function () {
    "use strict";

    var form = document.querySelector("form[data-upload-form]");
    if (!form || !window.FormData || !window.XMLHttpRequest) {
        return;
    }

    var input = form.querySelector("input[type=file]");
    var zone = form.querySelector("[data-dropzone]");
    var error = form.querySelector("[data-upload-error]");
    var progress = form.querySelector("[data-upload-progress]");
    var bar = progress.querySelector("progress");
    var label = progress.querySelector("output");
    var submit = form.querySelector("[data-upload-submit]");
    var max = parseInt(form.getAttribute("data-max-bytes"), 10);

    function showError(message) {
        error.textContent = message;
        error.hidden = false;
    }

    function tooLarge() {
        return input.files.length > 0 && isFinite(max) && input.files[0].size > max;
    }

    if (zone) {
        ["dragenter", "dragover"].forEach(function (name) {
            zone.addEventListener(name, function (event) {
                event.preventDefault();
                zone.classList.add("is-dragging");
            });
        });
        ["dragleave", "drop"].forEach(function (name) {
            zone.addEventListener(name, function () {
                zone.classList.remove("is-dragging");
            });
        });
        zone.addEventListener("drop", function (event) {
            event.preventDefault();
            if (event.dataTransfer && event.dataTransfer.files.length > 0) {
                input.files = event.dataTransfer.files;
                input.dispatchEvent(new Event("change"));
            }
        });
    }

    input.addEventListener("change", function () {
        error.hidden = true;
        if (tooLarge()) {
            showError(form.getAttribute("data-too-large"));
        }
    });

    form.addEventListener("submit", function (event) {
        event.preventDefault();
        error.hidden = true;
        if (!form.reportValidity()) {
            return;
        }
        if (tooLarge()) {
            showError(form.getAttribute("data-too-large"));
            return;
        }

        // FormData keeps the DOM order, so the file travels last, after the fields the server checks first.
        var data = new FormData(form);
        var token = form.querySelector("input[name=__RequestVerificationToken]");
        var request = new XMLHttpRequest();
        request.open("POST", form.getAttribute("action"));
        request.setRequestHeader("Accept", "application/json");
        if (token) {
            request.setRequestHeader("RequestVerificationToken", token.value);
        }

        submit.disabled = true;
        progress.hidden = false;
        request.upload.addEventListener("progress", function (update) {
            if (update.lengthComputable) {
                var percent = Math.floor(update.loaded * 100 / update.total);
                bar.value = percent;
                label.textContent = percent + "%";
            }
        });
        request.addEventListener("load", function () {
            var body = null;
            try {
                body = JSON.parse(request.responseText);
            } catch (ignored) {
                body = null;
            }
            if (request.status >= 200 && request.status < 300 && body && body.ok) {
                window.location.assign(body.redirect);
                return;
            }
            submit.disabled = false;
            progress.hidden = true;
            showError(body && body.message ? body.message : form.getAttribute("data-failed"));
        });
        request.addEventListener("error", function () {
            // The outcome is unknown; retrying is safe because the operation id is unchanged.
            submit.disabled = false;
            progress.hidden = true;
            showError(form.getAttribute("data-failed"));
        });
        request.send(data);
    });
})();
