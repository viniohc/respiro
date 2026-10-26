param([switch]$Publish,[string]$DotNetPath)
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
if(!$DotNetPath){
 $localSdk=Join-Path $PSScriptRoot '.sdk\dotnet.exe'
 if(Test-Path -LiteralPath $localSdk){$DotNetPath=$localSdk}
 else{
  $installed=Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
  if(!$installed){throw 'Instale o SDK .NET indicado em global.json. Nenhuma dependencia sera baixada automaticamente.'}
  $DotNetPath=$installed.Source
 }
}
$DotNetPath=(Resolve-Path -LiteralPath $DotNetPath).Path
$env:DOTNET_ROOT=Split-Path $DotNetPath -Parent
$env:DOTNET_CLI_HOME=Join-Path $PSScriptRoot '.cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_NOLOGO='1'
function Check-Space { if((Get-PSDrive C).Free -lt 12GB){throw 'Build bloqueado: manter 10 GiB livres mais 2 GiB de margem para o build.'} }
Check-Space
& $DotNetPath build .\src\Respiro.App\Respiro.App.csproj -c Release --nologo -m:1 -p:UseSharedCompilation=false
if($LASTEXITCODE -ne 0){throw 'Build do aplicativo falhou.'}
Check-Space
& $DotNetPath run --project .\tests\Respiro.Tests\Respiro.Tests.csproj -c Release -p:UseSharedCompilation=false
if($LASTEXITCODE -ne 0){throw 'Testes falharam.'}
if($Publish){Check-Space; & $DotNetPath publish .\src\Respiro.App\Respiro.App.csproj -c Release --no-restore --self-contained false -o .\artifacts\app -p:UseSharedCompilation=false; if($LASTEXITCODE -ne 0){throw 'Publicação falhou.'}}
Check-Space
