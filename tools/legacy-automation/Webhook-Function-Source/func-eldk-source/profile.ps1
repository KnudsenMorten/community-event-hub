# profile.ps1 — runs once per Function App instance on cold start
# Loads all secrets from Azure Function App Settings (environment variables)
# into global variables matching the original Secrets.psm1 structure

# ─── e-conomic ────────────────────────────────────────────────────────────────
$Global:Economic_headers_REST = @{
    'X-AppSecretToken'      = $env:ECONOMIC_APP_SECRET_TOKEN
    'X-AgreementGrantToken' = $env:ECONOMIC_AGREEMENT_GRANT_TOKEN
    'Content-Type'          = 'application/json'
    'charset'               = 'UTF-8'
}

# ─── SMTP (Brevo) ─────────────────────────────────────────────────────────────
$Global:SmtpUser = $env:SMTP_USER
$Global:SmtpPass = $env:SMTP_PASS

# ─── Webshop (WooCommerce + Company Manager / WordPress) ───────────────────────
# Used by webhook-syncorders (order -> e-conomic draft invoice).
$Global:ConsumerKey    = $env:WOOCOMMERCE_CONSUMER_KEY
$Global:ConsumerSecret = $env:WOOCOMMERCE_CONSUMER_SECRET
$Global:WpUserApi      = $env:COMPANY_MANAGER_WP_USER
$Global:WpAppPassword  = $env:COMPANY_MANAGER_WP_APP_PASSWORD

# ─── Currency conversion (One Simple API) ──────────────────────────────────────
$Global:Currency_APIKey = $env:CURRENCY_API_KEY

# ─── Shared config ─────────────────────────────────────────────────────────────
$Global:SmtpServer  = "smtp-relay.brevo.com"
$Global:SmtpPort    = 587
$Global:FromDisplay = "Experts Live Denmark"
$Global:FromAddress = "info@expertslive.dk"
$Global:AlertTo     = @("mok@expertslive.dk")
$Global:WebshopBaseUrl = if ($env:WEBSHOP_BASE_URL) { $env:WEBSHOP_BASE_URL } else { "https://expertslive.dk" }
$Global:EventName      = if ($env:ELDK_EVENT_NAME)  { $env:ELDK_EVENT_NAME }  else { "ELDK27" }
