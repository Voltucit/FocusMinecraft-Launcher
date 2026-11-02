param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$SkipPackage
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw '版本号格式无效。' }
$publicKey = Join-Path $repoRoot 'Build\Updates\update-public.pem'
$tools = Join-Path $repoRoot 'Build\FMLauncher.UpdateTools\FMLauncher.UpdateTools.csproj'
if (-not $SkipPackage) {
    if (-not (Test-Path -LiteralPath $publicKey)) { throw '尚未生成发布公钥，请先运行 UpdateTools keygen。' }
    dotnet run --project $tools -c Release -- verify-key --public-key $publicKey
    if ($LASTEXITCODE -ne 0) { throw '发布密钥校验失败。' }
}
$output = Join-Path $repoRoot "artifacts\release\$Version"
$publish = Join-Path $output 'app'
if (Test-Path -LiteralPath $publish) { throw "发布输出已存在，未覆盖：$publish" }
$project = Join-Path $repoRoot 'FocusMinecraftLauncher\FocusMinecraftLauncher.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained false -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw '启动器发布构建失败。' }
if (-not $SkipPackage) {
    dotnet run --project $tools -c Release -- pack --input $publish --output $output --version $Version
    if ($LASTEXITCODE -ne 0) { throw '签名更新包生成失败。' }
}
Write-Host "本地发布产物：$output"
