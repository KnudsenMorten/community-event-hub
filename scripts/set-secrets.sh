#!/usr/bin/env bash
# ===========================================================================
#  set-secrets.sh  -  Store the Community Hub secret VALUES into Key Vault
# ---------------------------------------------------------------------------
#  main.bicep provisions the Key Vault but deliberately stores no secret
#  values. This script sets them, AFTER deployment. It prompts for each value
#  so nothing secret is ever written to a file or to shell history.
#
#  The secret-name inventory matches CONTEXT.md section 11 and the names that
#  integrations.eldk27.json references.
#
#  Usage:   ./scripts/set-secrets.sh <dev|prod>
#
#  Prereq:  az login; the Key Vault already deployed by deploy.sh.
# ===========================================================================

set -euo pipefail

ENVIRONMENT="${1:-}"
if [[ "$ENVIRONMENT" != "dev" && "$ENVIRONMENT" != "prod" ]]; then
  echo "Usage: ./scripts/set-secrets.sh <dev|prod>" >&2
  exit 1
fi

# Derive baseName + RG from the matching parameter file so naming follows
# whatever the operator set there (eldkhub in this private repo ->
# rg-eldkhub-dev, communityhub in the public template -> rg-communityhub-dev).
# Falls back to 'communityhub' if jq missing.
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PARAM_FILE="${SCRIPT_DIR}/../infra/main.${ENVIRONMENT}.parameters.json"
if [[ -f "$PARAM_FILE" ]] && command -v jq >/dev/null 2>&1; then
  BASE_NAME="$(jq -r '.parameters.baseName.value // "communityhub"' "$PARAM_FILE")"
else
  BASE_NAME="communityhub"
fi
RESOURCE_GROUP="rg-${BASE_NAME}-${ENVIRONMENT}"

# Resolve the Key Vault name from the resource group (name is suffixed).
VAULT_NAME="$(az keyvault list \
  --resource-group "$RESOURCE_GROUP" \
  --query "[0].name" -o tsv 2>/dev/null)"

if [[ -z "$VAULT_NAME" ]]; then
  echo "ERROR: no Key Vault found in resource group '${RESOURCE_GROUP}'." >&2
  echo "       Either the RG doesn't exist (run scripts/deploy.sh ${ENVIRONMENT} first)" >&2
  echo "       OR baseName in infra/main.${ENVIRONMENT}.parameters.json doesn't match what was deployed." >&2
  echo "       Current resolved RG: ${RESOURCE_GROUP} (from baseName=${BASE_NAME})" >&2
  exit 1
fi

echo "Resolved resource group: ${RESOURCE_GROUP} (from baseName=${BASE_NAME})"

# RBAC self-grant. main.bicep provisions the KV with enableRbacAuthorization=true
# and grants the web/functions app MIs the Key Vault Secrets User role (GET+LIST).
# The deploying operator (you) needs Key Vault Secrets Officer (GET+LIST+SET+DELETE)
# to actually write secret values. Granted here once -- idempotent, deterministic
# role assignment name; re-running this script is a no-op for the role.
VAULT_ID="$(az keyvault show --name "$VAULT_NAME" --resource-group "$RESOURCE_GROUP" --query id -o tsv)"
OPERATOR_OID="$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)"
if [[ -z "$OPERATOR_OID" ]]; then
  # If signed-in as an SPN (not user), fall back to az account show -> SPN object id.
  OPERATOR_OID="$(az ad sp show --id "$(az account show --query user.name -o tsv)" --query id -o tsv 2>/dev/null || true)"
fi
if [[ -z "$OPERATOR_OID" ]]; then
  echo "ERROR: could not resolve current principal's object id. Make sure 'az login' completed." >&2
  exit 1
fi
SECRETS_OFFICER_ROLE='Key Vault Secrets Officer'
if ! az role assignment list --assignee "$OPERATOR_OID" --scope "$VAULT_ID" --role "$SECRETS_OFFICER_ROLE" --query "[0].id" -o tsv 2>/dev/null | grep -q .; then
  echo "Granting '${SECRETS_OFFICER_ROLE}' to operator (oid=${OPERATOR_OID}) on ${VAULT_NAME}..."
  az role assignment create --assignee-object-id "$OPERATOR_OID" --assignee-principal-type User --role "$SECRETS_OFFICER_ROLE" --scope "$VAULT_ID" --output none
  echo "Granted. Waiting 30s for the role to propagate to the data plane (Azure RBAC eventual consistency)..."
  sleep 30
fi

echo "Setting secrets in Key Vault: ${VAULT_NAME}"
echo "Leave a value blank to skip that secret (e.g. if not using that integration)."
echo

# Secret inventory: name -> human prompt.
SECRETS=(
  "sql-admin-password|SQL administrator password (the one used at deploy time)"
  "brevo-smtp-username|Brevo SMTP username (the Brevo-issued ID, e.g. 8xxxxxx@smtp-brevo.com)"
  "brevo-smtp-key|Brevo SMTP key (the SMTP password, NOT the account login)"
  "woocommerce-consumer-key|WooCommerce REST API consumer key (read-only)"
  "woocommerce-consumer-secret|WooCommerce REST API consumer secret"
  "company-manager-wp-user|Company Manager WordPress user (for the application password)"
  "company-manager-wp-app-password|Company Manager WordPress application password"
  "zoho-client-id|Zoho OAuth client ID (attendee reconciliation)"
  "zoho-client-secret|Zoho OAuth client secret"
  "zoho-refresh-token|Zoho OAuth refresh token"
)

for entry in "${SECRETS[@]}"; do
  NAME="${entry%%|*}"
  PROMPT="${entry#*|}"
  read -r -s -p "${PROMPT}: " VALUE
  echo
  if [[ -z "$VALUE" ]]; then
    echo "  skipped ${NAME}"
    continue
  fi
  az keyvault secret set \
    --vault-name "$VAULT_NAME" \
    --name "$NAME" \
    --value "$VALUE" \
    --output none
  echo "  set ${NAME}"
done

echo
echo "Done. Secret values are in Key Vault '${VAULT_NAME}'."
echo "The web app and Functions app read them via their managed identities."
