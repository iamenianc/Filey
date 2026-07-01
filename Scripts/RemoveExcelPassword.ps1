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
    # Never run workbook macros while automating untrusted files.
    try { $excel.AutomationSecurity = 3 } catch {}  # msoAutomationSecurityForceDisable
    try { $excel.EnableEvents = $false } catch {}

    # Sentinel for omitted optional COM parameters. Passing $null/$false for these makes
    # Excel mis-bind the remaining positional arguments, so the supplied open password is
    # NOT applied - Excel then silently prompts for it and Open fails even when the password
    # is correct. [Type]::Missing is the only value COM treats as "argument not supplied".
    $missing = [System.Type]::Missing

    foreach ($file in $Path) {
        $wb = $null
        try {
            if (-not (Test-Path -LiteralPath $file)) {
                $results.Add((New-Result $file $null $false "File not found."))
                continue
            }

            $outputPath = Get-CollisionSafePath $file

            # Open(Filename, UpdateLinks=0, ReadOnly=$false, Format=(missing),
            #      Password=$password, WriteResPassword=(missing), IgnoreReadOnlyRecommended=$true)
            try {
                $wb = $excel.Workbooks.Open($file, 0, $false, $missing, $password, $missing, $true)
            }
            catch {
                # Files from the internet/email open in Protected View, which a normal Open
                # cannot unlock. Retry through the Protected View window before giving up.
                $openError = $_
                try {
                    [void]$excel.ProtectedViewWindows.Open($file, $password)
                    $excel.ProtectedViewWindows.Item(1).Edit()
                    $wb = $excel.ActiveWorkbook
                } catch { $wb = $null }
                if ($null -eq $wb) { throw $openError }
            }

            # Clear the open password on the in-memory workbook.
            try { $wb.Password = "" } catch {}

            # Save a password-free copy. There is no settable WriteReservedPassword property on
            # Workbook; the write-reservation password is cleared via SaveAs's WriteResPassword
            # argument (Filename, FileFormat, Password="", WriteResPassword="").
            $wb.SaveAs($outputPath, $wb.FileFormat, "", "")
            $wb.Close($false)

            $results.Add((New-Result $file $outputPath $true $null))
        }
        catch {
            $message = $_.Exception.Message
            # Only relabel genuine wrong-password failures. HRESULT 0x800A03EC is the generic
            # "Open method failed" Excel raises when the password it was given is rejected.
            $hr = 0
            try { $hr = $_.Exception.HResult } catch {}
            if ($message -match 'The password you supplied is not correct' -or
                $message -match 'is not a valid password') {
                $message = "Incorrect password."
            }
            elseif ($hr -ne 0) {
                $message = "{0} (0x{1:X8})" -f $message, $hr
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
