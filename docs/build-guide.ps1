# Embeds the screenshots into the guide pages as data URIs.
#   guide.zh.src.html             -> guide.zh.html
#   guide.zh.src.html (styles) + guide.en.body.html -> guide.en.html
$shots = Join-Path $PSScriptRoot 'shots'
$utf8 = New-Object Text.UTF8Encoding $false
$zh = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'guide.zh.src.html'), [Text.Encoding]::UTF8)

# The English page reuses the Chinese page's styles with Latin fonts
$head = $zh.Substring(0, $zh.IndexOf('<main>'))
$head = $head.Replace('<title>跟檔夾操作說明</title>', '<title>FileFollow Guide</title>')
$head = $head.Replace('family=Noto+Sans+TC:wght@400;500;700&family=Noto+Serif+TC:wght@600;700', 'family=Source+Sans+3:wght@400;600;700&family=Source+Serif+4:opsz,wght@8..60,600;8..60,700')
$head = $head.Replace('"Noto Serif TC", "Songti TC", serif', '"Source Serif 4", Georgia, serif')
$head = $head.Replace('"Noto Sans TC", "Microsoft JhengHei", system-ui, sans-serif', '"Source Sans 3", "Segoe UI", system-ui, sans-serif')
$head = $head.Replace('content: "步驟 " counter(s)', 'content: "STEP " counter(s)')
$en = $head + [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'guide.en.body.html'), [Text.Encoding]::UTF8)

foreach ($pair in @(@('zh', $zh), @('en', $en))) {
  $lang = $pair[0]; $html = $pair[1]
  foreach ($k in 'folder', 'clip', 'pill') {
    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $shots "$lang-$k.png")))
    $html = $html.Replace('{{' + $k.ToUpper() + '}}', "data:image/png;base64,$b64")
  }
  $out = Join-Path $PSScriptRoot "guide.$lang.html"
  [IO.File]::WriteAllText($out, $html, $utf8)
  "$out " + (Get-Item $out).Length + " bytes"
}
