[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$DocumentationRoot = $PSScriptRoot
$BuildDirectory = Join-Path $DocumentationRoot 'build'
$PdfLatex = Get-Command pdflatex.exe -ErrorAction SilentlyContinue
if ($null -eq $PdfLatex) {
    throw 'pdfLaTeX was not found. Install MiKTeX or TeX Live, restart VS Code, and run this task again.'
}
New-Item -ItemType Directory -Path $BuildDirectory -Force | Out-Null
Push-Location -LiteralPath $DocumentationRoot
try {
    foreach ($Document in @('main', 'cheat_sheet', 'setup_guide')) {
        foreach ($Pass in 1..2) {
            & $PdfLatex.Source '-interaction=nonstopmode' '-halt-on-error' '-file-line-error' '-synctex=1' "-output-directory=$BuildDirectory" "$Document.tex"
            if ($LASTEXITCODE -ne 0) {
                throw "pdfLaTeX failed for $Document.tex (pass $Pass). See build\$Document.log."
            }
        }
    }
    Copy-Item -LiteralPath (Join-Path $BuildDirectory 'main.pdf') -Destination (Join-Path $DocumentationRoot 'EVA_Project_Documentation_EN.pdf') -Force
    Copy-Item -LiteralPath (Join-Path $BuildDirectory 'cheat_sheet.pdf') -Destination (Join-Path $DocumentationRoot 'EVA_Cheat_Sheet_EN.pdf') -Force
    Copy-Item -LiteralPath (Join-Path $BuildDirectory 'setup_guide.pdf') -Destination (Join-Path $DocumentationRoot 'EVA_Setup_Guide_EN.pdf') -Force
    Write-Host 'Built the full report, cheat sheet and step-by-step setup guide.'
}
finally {
    Pop-Location
}
