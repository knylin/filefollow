# Renders the panel screenshots used in the README and guide (zh + en) into docs/shots
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\FileFollow.exe'
$shots = Join-Path $PSScriptRoot 'shots'
New-Item -ItemType Directory -Force $shots | Out-Null
$now = Get-Date

$sets = @{
  zh = @{ dir = 'C:\Users\Public\Documents\客戶\品牌\2026\10月\素材'; sub = '參考圖'; current = '會議記錄.docx'
          files = [ordered]@{ 'AI生成_文案草稿.md'=-1; '主視覺_v3.psd'=-8; '會議記錄.docx'=-14; 'Banner_1200x628.png'=-40; '報價單_1029.xlsx'=-180; '提案簡報.pptx'=-1500; '主視覺_v2.psd'=-1600; '品牌規範.pdf'=-9000; 'logo.ai'=-30000 } }
  en = @{ dir = 'C:\Users\Public\Documents\Clients\Brand\2026\10\Assets'; sub = 'References'; current = 'Meeting notes.docx'
          files = [ordered]@{ 'AI draft copy.md'=-1; 'Key visual v3.psd'=-8; 'Meeting notes.docx'=-14; 'Banner_1200x628.png'=-40; 'Quote_1029.xlsx'=-180; 'Pitch deck.pptx'=-1500; 'Key visual v2.psd'=-1600; 'Brand guidelines.pdf'=-9000; 'logo.ai'=-30000 } }
}
foreach ($lang in 'zh', 'en') {
  $s = $sets[$lang]
  New-Item -ItemType Directory -Force (Join-Path $s.dir $s.sub) | Out-Null
  (Get-Item (Join-Path $s.dir $s.sub)).LastWriteTime = $now.AddMinutes(-300)
  foreach ($k in $s.files.Keys) {
    $f = Join-Path $s.dir $k
    'x' | Out-File $f
    (Get-Item $f).LastWriteTime = $now.AddMinutes($s.files[$k])
  }
  $env:FILEFOLLOW_LANG = $lang
  $out = Join-Path $shots "$lang.png"
  Start-Process -Wait $exe -ArgumentList '--snapshot', "`"$($s.dir)`"", "`"$(Join-Path $s.dir $s.current)`"", "`"$out`""
  Remove-Item "$out.txt" -ErrorAction SilentlyContinue
}
$env:FILEFOLLOW_LANG = $null
# Remove the demo folders again
Remove-Item 'C:\Users\Public\Documents\客戶', 'C:\Users\Public\Documents\Clients' -Recurse -Force
Get-ChildItem $shots | Select-Object Name, Length
