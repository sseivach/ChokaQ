param(
    [string]$DocsRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [switch]$RequireCompleteRussian
)

$ErrorActionPreference = 'Stop'

$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$message) {
    $failures.Add($message) | Out-Null
}

function Normalize-UrlPath([string]$path) {
    return ($path -replace '\\', '/')
}

function Strip-UrlNoise([string]$url) {
    $withoutTitle = ($url -split '\s+"')[0]
    $withoutHash = ($withoutTitle -split '#')[0]
    $withoutQuery = ($withoutHash -split '\?')[0]
    return $withoutQuery.Trim()
}

function Is-SkippedUrl([string]$url) {
    return [string]::IsNullOrWhiteSpace($url) -or
        $url.StartsWith('#') -or
        $url.StartsWith('http://') -or
        $url.StartsWith('https://') -or
        $url.StartsWith('mailto:')
}

function Resolve-PublicAsset([string]$absoluteUrl) {
    $relative = $absoluteUrl.TrimStart('/') -replace '/', [IO.Path]::DirectorySeparatorChar
    return Join-Path (Join-Path $DocsRoot 'public') $relative
}

function Get-RelativePathFromRoot([string]$root, [string]$path) {
    $rootUri = New-Object Uri (($root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar))
    $fileUri = New-Object Uri $path
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($fileUri).ToString()) -replace '/', [IO.Path]::DirectorySeparatorChar
}

$markdownFiles = Get-ChildItem $DocsRoot -Recurse -Filter '*.md' |
    Where-Object { $_.FullName -notmatch '\\node_modules\\' -and $_.FullName -notmatch '\\.vitepress\\dist\\' }

$englishMarkdownFiles = $markdownFiles |
    Where-Object { $_.FullName -notmatch '\\ru\\' }

$russianMarkdownFiles = $markdownFiles |
    Where-Object { $_.FullName -match '\\ru\\' }

$pageUrls = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($file in $markdownFiles) {
    $relative = Get-RelativePathFromRoot $DocsRoot $file.FullName
    $url = '/' + (Normalize-UrlPath ($relative -replace '\.md$', ''))
    if ($url -eq '/index') { $url = '/' }
    if ($url -eq '/ru/index') { $url = '/ru/' }
    $pageUrls.Add($url) | Out-Null
}

if (Test-Path (Join-Path $DocsRoot 'documentation-roadmap.md')) {
    Add-Failure 'Internal documentation-roadmap.md must not live under docs/. Move it to docs-internal/.'
}

$publicDocsTextFiles = @($markdownFiles.FullName) + @((Join-Path $DocsRoot '.vitepress/config.mts'))
foreach ($path in $publicDocsTextFiles) {
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $text = Get-Content -Raw -Encoding UTF8 -LiteralPath $path
    if ($text -match 'documentation-roadmap') {
        Add-Failure "Public docs reference internal roadmap: $path"
    }
}

$referencedPublicAssets = New-Object 'System.Collections.Generic.HashSet[string]'

foreach ($file in $markdownFiles) {
    $text = Get-Content -Raw -Encoding UTF8 -LiteralPath $file.FullName
    $text = [regex]::Replace($text, '(?s)```.*?```', '')

    foreach ($match in [regex]::Matches($text, '!\[[^\]]*\]\(([^)]+)\)')) {
        $url = Strip-UrlNoise $match.Groups[1].Value
        if (Is-SkippedUrl $url) { continue }

        if ($url.StartsWith('/diagrams/') -or $url.StartsWith('/screenshots/')) {
            if (-not $url.EndsWith('.png', [StringComparison]::OrdinalIgnoreCase)) {
                Add-Failure "Only PNG is allowed for diagram/screenshot references: $($file.FullName) -> $url"
            }
        }

        if ($url.StartsWith('/')) {
            $assetPath = Resolve-PublicAsset $url
            $referencedPublicAssets.Add($assetPath) | Out-Null
            if (-not (Test-Path -LiteralPath $assetPath)) {
                Add-Failure "Missing public asset: $($file.FullName) -> $url"
            }
        }
        else {
            $assetPath = Join-Path $file.DirectoryName ($url -replace '/', [IO.Path]::DirectorySeparatorChar)
            if (-not (Test-Path -LiteralPath $assetPath)) {
                Add-Failure "Missing relative image: $($file.FullName) -> $url"
            }
        }
    }

    foreach ($match in [regex]::Matches($text, '(?<!\!)\[[^\]]+\]\(([^)]+)\)')) {
        $url = Strip-UrlNoise $match.Groups[1].Value
        if (Is-SkippedUrl $url) { continue }

        if ($url.StartsWith('/')) {
            $extension = [IO.Path]::GetExtension($url)
            if ($extension -in @('.png', '.jpg', '.jpeg', '.gif', '.webp', '.ico')) {
                $assetPath = Resolve-PublicAsset $url
                if (-not (Test-Path -LiteralPath $assetPath)) {
                    Add-Failure "Missing public asset link: $($file.FullName) -> $url"
                }
            }
            else {
                $pageUrl = $url.TrimEnd('/')
                if ($pageUrl -eq '') { $pageUrl = '/' }
                if ($url -eq '/ru/') { $pageUrl = '/ru/' }
                if (-not $pageUrls.Contains($pageUrl)) {
                    Add-Failure "Missing page link: $($file.FullName) -> $url"
                }
            }
        }
        else {
            $relativeTarget = Join-Path $file.DirectoryName ($url -replace '/', [IO.Path]::DirectorySeparatorChar)
            if (-not (Test-Path -LiteralPath $relativeTarget)) {
                Add-Failure "Missing relative link: $($file.FullName) -> $url"
            }
        }
    }
}

$configPath = Join-Path $DocsRoot '.vitepress/config.mts'
if (Test-Path -LiteralPath $configPath) {
    $configText = Get-Content -Raw -Encoding UTF8 -LiteralPath $configPath
    foreach ($match in [regex]::Matches($configText, "link:\s*'([^']+)'")) {
        $url = Strip-UrlNoise $match.Groups[1].Value
        if (Is-SkippedUrl $url) { continue }
        $pageUrl = $url.TrimEnd('/')
        if ($pageUrl -eq '') { $pageUrl = '/' }
        if ($url -eq '/ru/') { $pageUrl = '/ru/' }
        if (-not $pageUrls.Contains($pageUrl)) {
            Add-Failure "Missing VitePress nav/sidebar page: $url"
        }
    }
}

$diagramFiles = Get-ChildItem (Join-Path $DocsRoot 'public/diagrams') -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne '.gitkeep' }
foreach ($file in $diagramFiles) {
    if ($file.Extension -ne '.png') {
        Add-Failure "Non-PNG diagram file found: $($file.FullName)"
    }
}

$screenshotFiles = Get-ChildItem (Join-Path $DocsRoot 'public/screenshots/the-deck') -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne '.gitkeep' }

try {
    Add-Type -AssemblyName System.Drawing
    foreach ($file in @($diagramFiles) + @($screenshotFiles)) {
        if ($file.Extension -ne '.png') { continue }
        $image = [System.Drawing.Image]::FromFile($file.FullName)
        try {
            if ($image.Width -ne 1920 -or $image.Height -ne 1080) {
                Add-Failure "PNG must be 1920x1080: $($file.FullName) is $($image.Width)x$($image.Height)"
            }
        }
        finally {
            $image.Dispose()
        }
    }
}
catch {
    Add-Failure "Could not validate PNG dimensions: $($_.Exception.Message)"
}

$allMarkdownText = ($markdownFiles | ForEach-Object { Get-Content -Raw -Encoding UTF8 -LiteralPath $_.FullName }) -join "`n"
foreach ($file in $screenshotFiles) {
    $url = '/screenshots/the-deck/' + $file.Name
    if ($allMarkdownText -notmatch [regex]::Escape($url)) {
        Add-Failure "Orphaned The Deck screenshot is not referenced by docs: $url"
    }
}

$deepDiveFiles = Get-ChildItem (Join-Path $DocsRoot '3-deep-dives') -Filter '*.md' -File -ErrorAction SilentlyContinue
foreach ($file in $deepDiveFiles) {
    $text = Get-Content -Raw -Encoding UTF8 -LiteralPath $file.FullName
    if ($text -notmatch '## Architecture Decision') {
        Add-Failure "Deep-dive article lacks Architecture Decision: $($file.FullName)"
    }
    if ($text -notmatch '(?i)## Additional Questions|### Additional questions') {
        Add-Failure "Deep-dive article lacks Additional Questions: $($file.FullName)"
    }
}

$russianDeepDiveRoot = Join-Path $DocsRoot 'ru/3-deep-dives'
$russianDeepDiveFiles = Get-ChildItem $russianDeepDiveRoot -Filter '*.md' -File -ErrorAction SilentlyContinue
$ruArchitectureDecision = '## ' + [string]::Concat([char[]]@(
    0x0410, 0x0440, 0x0445, 0x0438, 0x0442, 0x0435, 0x043A, 0x0442, 0x0443, 0x0440,
    0x043D, 0x043E, 0x0435, 0x0020, 0x0440, 0x0435, 0x0448, 0x0435, 0x043D, 0x0438, 0x0435
))
$ruAdditionalQuestions = '## ' + [string]::Concat([char[]]@(
    0x0414, 0x043E, 0x043F, 0x043E, 0x043B, 0x043D, 0x0438, 0x0442, 0x0435, 0x043B,
    0x044C, 0x043D, 0x044B, 0x0435, 0x0020, 0x0432, 0x043E, 0x043F, 0x0440, 0x043E, 0x0441, 0x044B
))
foreach ($file in $russianDeepDiveFiles) {
    $text = Get-Content -Raw -Encoding UTF8 -LiteralPath $file.FullName
    if ($text -notmatch [regex]::Escape($ruArchitectureDecision)) {
        Add-Failure "Russian deep-dive article lacks required Architecture Decision heading: $($file.FullName)"
    }
    if ($text -notmatch [regex]::Escape($ruAdditionalQuestions)) {
        Add-Failure "Russian deep-dive article lacks required Additional Questions heading: $($file.FullName)"
    }
}

$ruRoot = Join-Path $DocsRoot 'ru'
foreach ($file in $russianMarkdownFiles) {
    $relative = Get-RelativePathFromRoot $ruRoot $file.FullName
    $englishPeer = Join-Path $DocsRoot $relative
    if (-not (Test-Path -LiteralPath $englishPeer)) {
        Add-Failure "Russian page has no English source peer: $($file.FullName)"
    }
}

if ($RequireCompleteRussian) {
    foreach ($file in $englishMarkdownFiles) {
        $relative = Get-RelativePathFromRoot $DocsRoot $file.FullName
        $russianPeer = Join-Path $ruRoot $relative
        if (-not (Test-Path -LiteralPath $russianPeer)) {
            Add-Failure "Missing Russian translation: $relative"
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host 'Documentation validation failed:' -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }
    exit 1
}

Write-Host 'Documentation validation passed.' -ForegroundColor Green
