<#
.SYNOPSIS
  Wrapper del CLI Python del driver AICopilot.

.EXAMPLE
  .\tools\aicopilot_driver.ps1 health
  .\tools\aicopilot_driver.ps1 intercept --index 1 --station TailHigh
#>
$ErrorActionPreference = "Stop"
$py = Join-Path $PSScriptRoot "aicopilot_driver.py"
& python $py @args
exit $LASTEXITCODE
