Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead("$PSScriptRoot\spec-migrace-azure-functions.docx")
$e = $z.GetEntry('word/document.xml')
$r = New-Object System.IO.StreamReader($e.Open())
$x = [xml]$r.ReadToEnd()
$r.Close()
$z.Dispose()
$ns = New-Object System.Xml.XmlNamespaceManager($x.NameTable)
$ns.AddNamespace('w','http://schemas.openxmlformats.org/wordprocessingml/2006/main')
foreach ($p in $x.SelectNodes('//w:p', $ns)) {
    $texts = $p.SelectNodes('.//w:t', $ns) | ForEach-Object { $_.InnerText }
    $line = $texts -join ''
    if ($line.Trim()) { Write-Output $line }
}
