<#
.SYNOPSIS
  Regenerate the InfosysNavigator.HardwareSections slug set from the live infosys menu tree.

.DESCRIPTION
  find_hardware maps a Beckhoff order number to a documentation section slug
  (InfosysNavigator.HardwareSections) and navigates from there. The slug scheme is irregular
  (family wildcards "el30xx", exact products "epp3504", underscore groups "el10xx_el11xx",
  hyphenated order-specific slugs "epp7342-0002"), so the list cannot be derived arithmetically;
  it is enumerated from infosys's own menu.php tree.

  This script walks one seed page per product family, fetches that family's menu subtree, and
  extracts every /content/1033/<slug>/ directory it references, filtered to the family prefix.
  It prints the slugs grouped by family, ready to paste into HardwareSections. It does NOT edit
  the .cs file (review the diff and paste deliberately). Re-run when Beckhoff adds products.

  menu.php only expands the subtree around the page you query, so each family needs an in-family
  seed page (the EtherCAT Box landing, for instance, does not expand the EP subtree on its own).

.EXAMPLE
  pwsh dotnet/oracle/regen-hardware-sections.ps1
#>
[CmdletBinding()]
param(
    [int] $TimeoutSec = 30
)

$ErrorActionPreference = 'Stop'
$Host_ = 'https://infosys.beckhoff.com'

# family label -> (seed section index path under content/1033/, prefix regex to keep)
$families = [ordered]@{
    'EtherCAT Terminals (EL/EM/ELM/ED)' = @{ Seed = 'el30xx/index.html';                  Prefix = '^(el|em|elm|es|ed)\d' }
    'EtherCAT couplers / infra (EK)'    = @{ Seed = 'ek18xx/index.html';                   Prefix = '^ek' }
    'EtherCAT Box (EP)'                 = @{ Seed = 'ep1xxx/index.html';                   Prefix = '^ep\d' }
    'EtherCAT Box rugged (ER)'          = @{ Seed = 'erxxxx/index.html';                   Prefix = '^er' }
    'EtherCAT Box 24V (EQ)'             = @{ Seed = 'eqxxxx/index.html';                   Prefix = '^eq' }
    'EtherCAT P Box (EPP)'              = @{ Seed = 'epp1xxx/index.html';                  Prefix = '^epp' }
    'EtherCAT plug-in modules (EJ)'     = @{ Seed = 'ej31xx/index.html';                   Prefix = '^ej\d' }
    'IO-Link box (EPI/ERI)'             = @{ Seed = 'epi1xxx/index.html';                  Prefix = '^(epi|eri)' }
    'Infrastructure / switches (CU)'    = @{ Seed = 'cu2508/index.html';                   Prefix = '^cu' }
}

function Get-PrimaryId([string] $html) {
    if ($html -match 'name="?primaryid"?\s+content="([^"]+)"') { return $matches[1] }
    return $null
}

function Get-FamilySlugs([string] $seed, [string] $prefix) {
    $idx = Invoke-WebRequest -Uri "$Host_/content/1033/$seed" -UseBasicParsing -TimeoutSec $TimeoutSec
    $primaryId = Get-PrimaryId $idx.Content
    if (-not $primaryId) { throw "no primaryid for seed $seed" }

    $content = [uri]::EscapeDataString("../content/1033/$seed")
    $menuUrl = "$Host_/english/menu/menu.php?content=$content&id=$primaryId"
    $menu = Invoke-WebRequest -Uri $menuUrl -UseBasicParsing -TimeoutSec $TimeoutSec

    $slugs = [regex]::Matches($menu.Content, '/content/1033/([a-z0-9]+(?:[_-][a-z0-9]+)*)/') |
        ForEach-Object { $_.Groups[1].Value }
    return $slugs | Sort-Object -Unique | Where-Object { $_ -match $prefix }
}

Write-Host "// Regenerated $(Get-Date -Format o) from $Host_ menu.php tree." -ForegroundColor Cyan
Write-Host '// Paste into InfosysNavigator.HardwareSections (keep "ethercatsystem" at the top).'
Write-Host ''

foreach ($label in $families.Keys) {
    $f = $families[$label]
    try {
        $slugs = Get-FamilySlugs $f.Seed $f.Prefix
        Write-Host "        // $label  ($($slugs.Count))"
        $line = ($slugs | ForEach-Object { "`"$_`"" }) -join ', '
        Write-Host "        $line,"
    } catch {
        Write-Warning "$label (seed $($f.Seed)): $($_.Exception.Message)"
    }
}
