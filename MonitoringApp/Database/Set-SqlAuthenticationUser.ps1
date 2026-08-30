[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateLength(1, 128)]
    [string]$Username,

    [Parameter()]
    [Security.SecureString]$Password,

    [Parameter()]
    [ValidateSet('Reader', 'Operator', 'Admin')]
    [string]$Role = 'Admin',

    [Parameter()]
    [string]$ConnectionString
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($null -eq $Password) {
    $Password = Read-Host 'Password' -AsSecureString
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    $settingsPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'appsettings.Development.json'
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $ConnectionString = [string]$settings.ConnectionStrings.AlertsDatabase
}

$normalizedUsername = $Username.Trim()
if ($normalizedUsername.Length -eq 0 -or $normalizedUsername.Length -gt 128) {
    throw 'Username is required and may contain at most 128 characters.'
}

$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password)
try {
    $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    if ([string]::IsNullOrEmpty($plainPassword)) {
        throw 'Password must not be empty.'
    }

    $salt = New-Object byte[] 16
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($salt)
    }
    finally {
        $random.Dispose()
    }

    $passwordBytes = [Text.Encoding]::UTF8.GetBytes($plainPassword)
    $deriveBytes = [Security.Cryptography.Rfc2898DeriveBytes]::new(
        $passwordBytes,
        $salt,
        600000,
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $key = $deriveBytes.GetBytes(32)
    }
    finally {
        $deriveBytes.Dispose()
    }

    $passwordHash = 'PBKDF2-SHA256$600000${0}${1}' -f `
        [Convert]::ToBase64String($salt),
        [Convert]::ToBase64String($key)
}
finally {
    if ($null -ne $passwordPointer) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }
    $plainPassword = $null
}

$connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = @'
IF OBJECT_ID(N'dbo.AuthenticationUsers', N'U') IS NULL
    THROW 50010, 'AuthenticationUsers does not exist. Apply the database migration first.', 1;

UPDATE dbo.AuthenticationUsers
SET PasswordHash = @passwordHash,
    Role = @role
WHERE Username = @username;

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO dbo.AuthenticationUsers (Username, PasswordHash, Role)
    VALUES (@username, @passwordHash, @role);
END;
'@
    [void]$command.Parameters.Add('@username', [System.Data.SqlDbType]::NVarChar, 128)
    [void]$command.Parameters.Add('@passwordHash', [System.Data.SqlDbType]::NVarChar, 512)
    [void]$command.Parameters.Add('@role', [System.Data.SqlDbType]::NVarChar, 16)
    $command.Parameters['@username'].Value = $normalizedUsername
    $command.Parameters['@passwordHash'].Value = $passwordHash
    $command.Parameters['@role'].Value = $Role
    [void]$command.ExecuteNonQuery()
    Write-Host "SQL authentication user '$normalizedUsername' was created or updated." -ForegroundColor Green
}
finally {
    $connection.Dispose()
    $passwordHash = $null
}
