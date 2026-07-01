<#
    Removes the open/write password from one or more Excel workbooks via Excel COM
    Automation and saves each as a new "(pw removed)" copy alongside the original.
    Must be run under Windows PowerShell (powershell.exe), not PowerShell 7 (pwsh.exe) -
    Excel COM automation requires an STA thread, which powershell.exe uses by default.

    The password is intentionally NOT a parameter: it is read once from STDIN so it
    never appears in this process's command line (Task Manager, Win32_Process, etc.).

    STDOUT carries exactly one JSON array (one object per input file):
        [ { "path": "...", "outputPath": "...", "success": true,  "error": null }, ... ]
    All diagnostics go to STDERR/Write-Warning so STDOUT stays pure JSON.
#>
param(
    [Parameter(Mandatory = $true)]
    [string[]] $Path
)

function Get-CollisionSafePath([string]$originalPath) {
    $dir  = Split-Path -Path $originalPath -Parent
    $name = [System.IO.Path]::GetFileNameWithoutExtension($originalPath)
    $ext  = [System.IO.Path]::GetExtension($originalPath)

    $candidate = Join-Path $dir "$name (pw removed)$ext"
    $n = 2
    while (Test-Path -LiteralPath $candidate) {
        $candidate = Join-Path $dir "$name (pw removed) ($n)$ext"
        $n++
    }
    return $candidate
}

function New-Result([string]$path, [string]$outputPath, [bool]$success, [string]$errorMessage) {
    return [pscustomobject]@{
        path       = $path
        outputPath = $outputPath
        success    = $success
        error      = $errorMessage
    }
}

# Windows PowerShell 5.1's ConvertTo-Json collapses a single-item collection to a bare
# JSON object instead of a one-element array (the -AsArray fix for this only exists in
# PowerShell 6.2+/pwsh.exe, which we can't use here since Excel COM automation requires
# the STA thread that only powershell.exe provides by default). Wrap manually instead.
function Write-JsonResults([System.Collections.Generic.List[object]]$items) {
    $json = $items | ConvertTo-Json -Compress
    if ($items.Count -eq 1) {
        $json = "[$json]"
    }
    Write-Output $json
}

$results = New-Object System.Collections.Generic.List[object]

$password = [Console]::In.ReadLine()
if ([string]::IsNullOrEmpty($password)) {
    foreach ($file in $Path) {
        $results.Add((New-Result $file $null $false "No password was supplied."))
    }
    Write-JsonResults $results
    return
}

$excel = $null
try {
    try {
        $excel = New-Object -ComObject Excel.Application
    }
    catch {
        foreach ($file in $Path) {
            $results.Add((New-Result $file $null $false "Excel is not installed or could not be started."))
        }
        Write-JsonResults $results
        return
    }

    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    $excel.AskToUpdateLinks = $false

    foreach ($file in $Path) {
        $wb = $null
        try {
            $outputPath = Get-CollisionSafePath $file
            $wb = $excel.Workbooks.Open($file, $false, $false, $null, $password)
            $wb.Password = ""
            $wb.WriteReservedPassword = ""
            $wb.SaveAs($outputPath, $wb.FileFormat)
            $wb.Close($false)

            $results.Add((New-Result $file $outputPath $true $null))
        }
        catch {
            $message = $_.Exception.Message
            if ($message -match 'password' -or $message -match 'is not valid') {
                $message = "Incorrect password."
            }
            $results.Add((New-Result $file $null $false $message))
        }
        finally {
            if ($wb) {
                try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($wb) } catch {}
                $wb = $null
            }
        }
    }
}
finally {
    if ($excel) {
        try { $excel.Quit() } catch {}
        try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($excel) } catch {}
        $excel = $null
    }
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
}

Write-JsonResults $results
