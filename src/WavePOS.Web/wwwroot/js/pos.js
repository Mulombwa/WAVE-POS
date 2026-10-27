// WAVE POS browser helpers.
window.wavePos = {
    // Loads a document page in a hidden frame and opens the browser's print dialog for it.
    // A frame (rather than a new tab) isn't stopped by pop-up blockers.
    printUrl(url) {
        const old = document.getElementById("wavepos-print-frame");
        if (old) old.remove();

        const frame = document.createElement("iframe");
        frame.id = "wavepos-print-frame";
        frame.setAttribute("aria-hidden", "true");
        frame.style.cssText = "position:fixed;right:0;bottom:0;width:0;height:0;border:0;visibility:hidden;";
        frame.onload = () => {
            frame.contentWindow.focus();
            frame.contentWindow.print();
        };
        frame.src = url + (url.includes("?") ? "&" : "?") + "embed=1";
        document.body.appendChild(frame);
    },

    // Prints a document that's already showing in a preview frame.
    printFrame(id) {
        const frame = document.getElementById(id);
        if (!frame) throw new Error("Preview not found");
        frame.contentWindow.focus();
        frame.contentWindow.print();
    }
};
