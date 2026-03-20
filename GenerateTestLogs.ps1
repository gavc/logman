# 1. Check for Administrative privileges
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Please run this script as Administrator to register event sources and write to the System log."
    return
}

$AppName = "LogManTest"
$SysName = "LogManSystemTest"

Write-Host "--- LogMan Test Data Generator ---" -ForegroundColor Cyan

# 2. Register/Prepare Application Source
if (-not [System.Diagnostics.EventLog]::SourceExists($AppName)) {
    New-EventLog -LogName "Application" -Source $AppName
    Write-Host "[+] Registered source '$AppName' to Application log." -ForegroundColor Gray
}

# 3. Register/Prepare System Source
if (-not [System.Diagnostics.EventLog]::SourceExists($SysName)) {
    New-EventLog -LogName "System" -Source $SysName
    Write-Host "[+] Registered source '$SysName' to System log." -ForegroundColor Gray
}

# 4. Generate Application Log Entries
Write-Host "Writing to Application log..." -ForegroundColor Yellow
Write-EventLog -LogName "Application" -Source $AppName -EventID 100 -EntryType Information -Message "LogMan Test: This is a standard Information event for UI testing."
Write-EventLog -LogName "Application" -Source $AppName -EventID 101 -EntryType Warning -Message "LogMan Test: This is a Warning event (should appear Orange in LogMan)."
Write-EventLog -LogName "Application" -Source $AppName -EventID 102 -EntryType Error -Message "LogMan Test: This is an Error event (should appear Red/Bold in LogMan)."

# 5. Generate System Log Entries
Write-Host "Writing to System log..." -ForegroundColor Yellow
Write-EventLog -LogName "System" -Source $SysName -EventID 200 -EntryType Information -Message "LogMan Test: System-level notification generated successfully."

# 6. Trigger Security Log Entry
# Direct writing to Security is restricted. We trigger a standard audit event by checking privileges.
Write-Host "Triggering Security Audit event (Privilege Check)..." -ForegroundColor Yellow
whoami /priv > $null

Write-Host "`n[SUCCESS] Test entries generated! Toggle 'Live' in LogMan to see them." -ForegroundColor Green
