// A keyboard-wedge scanner may emit the ASCII group separator as Ctrl+].
document.addEventListener("keydown", event => {
    const input = event.target;
    const isMarkingField = input instanceof HTMLTextAreaElement && input.classList.contains("marking-scans");
    const isPosScanner = input instanceof HTMLInputElement && input.id === "pos-scan";
    if (!isMarkingField && !isPosScanner)
        return;
    if (event.key === "\u001d" || (event.ctrlKey && !event.altKey && (event.key === "]" || event.code === "BracketRight"))) {
        event.preventDefault();
        input.setRangeText("\u001d", input.selectionStart, input.selectionEnd, "end");
        input.dispatchEvent(new Event("input", { bubbles: true }));
    }
});
