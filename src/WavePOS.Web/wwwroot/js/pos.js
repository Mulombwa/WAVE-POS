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

// Saves text (e.g. a JSON export or CSV) as a file download.
window.wavePos.download = function (fileName, contentType, text) {
    const blob = new Blob([text], { type: contentType });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(link.href), 1000);
};
