param(
    [Parameter(Mandatory = $true)]
    [string]$version,
    [switch]$delete,
    [switch]$push
)

function IsValidVersion($v) {
    return $v -match '^\d+\.\d+\.\d+(\.\d+)?(-[a-zA-Z0-9\-\.]+)?$'
}

function TagExistsLocal($tagName) {
    return git tag | Where-Object { $_ -eq $tagName }
}

function TagExistsRemote($tagName) {
    return git ls-remote --tags origin | Select-String "refs/tags/$tagName"
}

function CreateSignedTag($v) {
    $tagName = "v$v"
    $tagMessage = "Release version $v"

    if (TagExistsLocal $tagName) {
        Write-Warning "⚠️ The local tag '$tagName' exists already. Use -delete to remove it or provide a new value."
        return $false
    }

    try {
        git tag -s $tagName -m $tagMessage
        Write-Host "✅ Tag '$tagName' successfully created and signed."
        return $true
    } catch {
        Write-Warning "⚠️ Error signing tag '$tagName'."
        $choice = Read-Host "Do you want retry with another version (r) or forces creation without signing (f)? [r/f]"
        if ($choice -match '^[rR]$') {
            $newVersion = Read-Host "Provides a new version"
            if (IsValidVersion($newVersion)) {
                return CreateSignedTag $newVersion
            } else {
                Write-Error "❌ Invalid version. Quitting."
                exit 1
            }
        } elseif ($choice -match '^[fF]$') {
            git tag $tagName -m $tagMessage
            Write-Host "⚠️ Tag '$tagName' create without signature."
            return $true
        } else {
            Write-Error "❌ Invalid option. Quitting."
            exit 1
        }
    }
}

function PushTag($v) {
    $tagName = "v$v"

    if (TagExistsRemote $tagName) {
        Write-Warning "⚠️ The remote tag '$tagName' exists already."
        return $false
    }

    try {
        git push origin $tagName
        Write-Host "🚀 Tag '$tagName' successfully sent to remote."
        return $true
    } catch {
        Write-Error "❌ Error sending tag '$tagName' to remote."
        return $false
    }
}

function DeleteRemoteTag($v) {
    $tagName = "v$v"
    try {
        git push origin --delete $tagName
        Write-Host "🗑️ Remote tag '$tagName' successfully deleted."
        return $true
    } catch {
        Write-Warning "⚠️ Error removing remote tag '$tagName'. It may not exists."
        return $false
    }
}

function DeleteLocalTag($v) {
    $tagName = "v$v"
    try {
        git tag -d $tagName
        Write-Host "🗑️ Local tag '$tagName' successfully removed."
        return $true
    } catch {
        Write-Warning "⚠️ Error removing local tag '$tagName'. It may not exists."
        return $false
    }
}

# Execução principal
if (-not (IsValidVersion $version)) {
    Write-Error "❌ Invalid tag version. Use the format X.X.X, X.X.X.X or X.X.X.X-* for prerelease tags"
    exit 1
}

$tagName = "v$version"

if ($push -and $delete) {
    Write-Host '🔁 Combined: deleting and recreating the tag $tagName ...'
    DeleteRemoteTag $version | Out-Null
    DeleteLocalTag $version | Out-Null

    if (CreateSignedTag $version) {
        PushTag $version | Out-Null
    }
    Exit 0;
}

if ($push) {
    if (CreateSignedTag $version) {
        PushTag $version | Out-Null
    }
    Exit 0;
}
if ($delete) {
    DeleteRemoteTag $version | Out-Null
    DeleteLocalTag $version | Out-Null
    Exit 0;
}

Write-Error '❌ Invalid option. Use -push, -delete or both to delete and push in a single action.'
exit 1
