# Quick Start: Generate & Store AppInsights Tokens

This guide will get your scouts working with AppInsights in 5 minutes.

## Prerequisites

- Docker stack running: `docker compose up --build`
- Portal accessible at `http://localhost:5080`
- `az login` available on your machine

## Step 1: Generate Arrivia Token

```powershell
# Log in to Arrivia tenant
az login --tenant "60396ff5-e90f-4bc5-aced-a4f462f2636e"

# Get the access token
$token = (az account get-access-token --query accessToken -o tsv)
Write-Host "Token: $token"
```

## Step 2: Store Token in Portal

1. Open browser: http://localhost:5080/
2. Navigate to **Settings** tab (top navigation)
3. Find **AppInsights Tokens** section
4. Click **+ Add Token**
5. Fill in:
   - **Company/TenantKey**: `arrivia`
   - **Token**: Paste the token from step 1
   - **Notes**: "Initial Arrivia token"
6. Click **Save**

You should see: ✅ "Token saved successfully"

## Step 3: Test Scout Execution

1. Go to **Simian Army** tab
2. Click **Run Now** on **Latency Scout Monkey**
3. You should see:
   - Status changes from "error" to a result
   - Log output showing AppInsights queries executed
   - Findings (if any issues detected)

## Step 4 (Optional): Add Ice Token

```powershell
# Log in to Ice tenant
az login --tenant "b0298d22-1db0-4550-8fa6-a116a5ab4208"

# Get the access token
$token = (az account get-access-token --query accessToken -o tsv)
Write-Host "Token: $token"
```

Repeat **Step 2**, but use:
- **Company/TenantKey**: `ice`
- **Token**: Paste the Ice token

## Troubleshooting

### "Token is invalid or expired"
- The token expires after ~1 hour
- Generate a fresh token using the commands above
- Re-paste into Portal

### "The given key was not present in the dictionary"
- The TenantKey might not match (should be "arrivia" or "ice")
- Check the dropdown for available companies

### Scout still shows "error"
- Refresh the browser: F5
- Check the Functions logs: `docker compose logs functions --tail 30`
- Look for credential errors

## What's Happening Behind the Scenes

```
You → az login → Token → Portal UI → DataProtection Encrypt 
  → SQL Storage → Functions Container → Decrypt → BearerTokenCredential 
  → AppInsights API → Scout Findings
```

Each token:
- ✅ Is encrypted before storage (DataProtection)
- ✅ Is per-tenant (Arrivia vs Ice)
- ✅ Is valid for ~1 hour (auto-refresh in future versions)
- ✅ Never leaves the local network (runs in Docker)

## Manual Token Test

If you want to verify the endpoint works:

```powershell
$url = "http://localhost:5080/api/appinsights/tokens"
$headers = @{
    "Content-Type" = "application/json"
}
$body = @{
    tenantKey = "arrivia"
    token = $token
} | ConvertTo-Json

Invoke-WebRequest -Uri $url -Method POST -Headers $headers -Body $body
```

## Architecture Notes

- **Web app** (Kestrel on :5080): Encrypts and stores tokens
- **Functions app** (Azure Functions Worker on :80 inside Docker): Uses tokens to query AppInsights
- **SQL database**: Stores encrypted tokens in `dbo.AppInsightsTokens` table
- **Shared encryption key**: Both apps use same DataProtection configuration

Tokens are **never stored in plain text** and **never leave the Docker network**.

---

**Next**: Once tokens are stored, scouts will automatically query AppInsights and generate findings. Check the Scouts tab for results!
