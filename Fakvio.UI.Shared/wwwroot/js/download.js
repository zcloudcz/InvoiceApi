// JS interop helper for triggering file downloads from Blazor.
// Called from C# code via IJSRuntime.InvokeVoidAsync("downloadFile", fileName, base64Content, mimeType).
window.downloadFile = function (fileName, base64Content, mimeType) {
    // Convert the base64 string back to binary data
    var byteCharacters = atob(base64Content);
    var byteNumbers = new Array(byteCharacters.length);
    for (var i = 0; i < byteCharacters.length; i++) {
        byteNumbers[i] = byteCharacters.charCodeAt(i);
    }
    var byteArray = new Uint8Array(byteNumbers);

    // Create a Blob from the binary data with the specified MIME type
    var blob = new Blob([byteArray], { type: mimeType });

    // Create a temporary anchor element to trigger the download
    var link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = fileName;
    document.body.appendChild(link);
    link.click();

    // Clean up: remove the link and revoke the object URL
    document.body.removeChild(link);
    URL.revokeObjectURL(link.href);
};
