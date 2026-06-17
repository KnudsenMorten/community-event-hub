#!/usr/bin/env bash
# ===========================================================================
#  deploy.sh  -  Deploy the Community Hub Azure infrastructure (Stage 1)
# ---------------------------------------------------------------------------
#  Creates the resource group and deploys infra/main.bicep into it.
#  No SQL admin password is needed: the SQL server is Azure-AD-only and the app
#  + Functions authenticate via their managed identities (passwordless).
#
#  Usage:
#     ./scripts/deploy.sh dev          # deploy the dev environment
#     ./scripts/deploy.sh prod         # deploy the prod environment
#     ./scripts/deploy.sh dev --whatif # preview changes, deploy nothing
#
#  Prerequisites:
#     - Azure CLI (az) installed and logged in:  az login
#     - Bicep CLI (bundled with recent az):      az bicep version
#
#  Subscription:
#     The target subscription is REQUIRED via the AZURE_SUBSCRIPTION_ID env
#     var -- there is no built-in default, so a deploy can never land in an
#     unintended subscription. Both dev + prod RGs are expected to live in
#     the same sub (env separation is by RG, not by subscription):
#       AZURE_SUBSCRIPTION_ID=<your-sub-id> ./scripts/deploy.sh dev
#     The script selects the subscription explicitly before deploying.
#
#  See docs/RUNBOOK.md for the full deploy + post-deploy procedure.
# ===========================================================================

set -euo pipefail

# --- Arguments -------------------------------------------------------------

ENVIRONMENT="${1:-}"
WHATIF_FLAG="${2:-}"

if [[ "$ENVIRONMENT" != "dev" && "$ENVIRONMENT" != "prod" ]]; then
  echo "ERROR: first argument must be 'dev' or 'prod'." >&2
  echo "Usage: ./scripts/deploy.sh <dev|prod> [--whatif]" >&2
  exit 1
fi

# --- Configuration ---------------------------------------------------------

LOCATION="westeurope"

# Target subscription -- REQUIRED via the AZURE_SUBSCRIPTION_ID env var (no
# default, so a deploy cannot land in the wrong subscription). Both dev + prod
# RGs are expected in the same sub -- env separation is by RG, not by sub.
AZURE_SUBSCRIPTION_ID="${AZURE_SUBSCRIPTION_ID:-}"
if [[ -z "$AZURE_SUBSCRIPTION_ID" ]]; then
  echo "ERROR: set AZURE_SUBSCRIPTION_ID to the target subscription id." >&2
  echo "       e.g. AZURE_SUBSCRIPTION_ID=<your-sub-id> ./scripts/deploy.sh ${ENVIRONMENT}" >&2
  exit 1
fi

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
INFRA_DIR="${SCRIPT_DIR}/../infra"
TEMPLATE="${INFRA_DIR}/main.bicep"
PARAM_FILE="${INFRA_DIR}/main.${ENVIRONMENT}.parameters.json"

# Derive baseName + RG from the parameter file so naming follows whatever the
# operator set there (eldkhub in this private repo -> rg-eldkhub-dev,
# communityhub in the public template -> rg-communityhub-dev). Falls back to
# 'communityhub' if jq missing -- the per-file pre-flight check below catches
# a truly absent parameter file. RG name is what determines where the
# resources land; getting it wrong silently provisions into the wrong place.
if [[ -f "$PARAM_FILE" ]] && command -v jq >/dev/null 2>&1; then
  BASE_NAME="$(jq -r '.parameters.baseName.value // "communityhub"' "$PARAM_FILE")"
else
  BASE_NAME="communityhub"
fi

RESOURCE_GROUP="rg-${BASE_NAME}-${ENVIRONMENT}"
DEPLOYMENT_NAME="${BASE_NAME}-${ENVIRONMENT}-$(date +%Y%m%d-%H%M%S)"

# --- Pre-flight checks -----------------------------------------------------

if ! command -v az >/dev/null 2>&1; then
  echo "ERROR: Azure CLI (az) is not installed." >&2
  exit 1
fi

if ! az account show >/dev/null 2>&1; then
  echo "ERROR: not logged in to Azure. Run 'az login' first." >&2
  exit 1
fi

# Select the target subscription explicitly so the deploy cannot land in the
# wrong one (AZURE_SUBSCRIPTION_ID was required + validated above).
if ! az account set --subscription "$AZURE_SUBSCRIPTION_ID" 2>/dev/null; then
  echo "ERROR: could not select subscription ${AZURE_SUBSCRIPTION_ID}." >&2
  echo "       Check the id and that your account has access to it." >&2
  exit 1
fi

if [[ ! -f "$TEMPLATE" ]]; then
  echo "ERROR: template not found: $TEMPLATE" >&2
  exit 1
fi

if [[ ! -f "$PARAM_FILE" ]]; then
  echo "ERROR: parameter file not found: $PARAM_FILE" >&2
  exit 1
fi

# --- SQL admin password: not required -------------------------------------
#  The SQL server is Azure-AD-only; the app + Functions authenticate via their
#  managed identities. No SQL login or password is provisioned, so main.bicep
#  no longer takes a sqlAdminPassword parameter and nothing is prompted here.

# --- Summary ---------------------------------------------------------------

SUBSCRIPTION_NAME="$(az account show --query name -o tsv)"
echo "-----------------------------------------------------------------"
echo " Community Hub infrastructure deployment"
echo "   Environment    : ${ENVIRONMENT}"
echo "   Subscription   : ${SUBSCRIPTION_NAME}"
echo "   Subscription id: ${AZURE_SUBSCRIPTION_ID}"
echo "   Resource group : ${RESOURCE_GROUP}  (${LOCATION})"
echo "   Template       : ${TEMPLATE}"
echo "   Mode           : ${WHATIF_FLAG:-deploy}"
echo "-----------------------------------------------------------------"

# --- Resource group --------------------------------------------------------

echo "Ensuring resource group '${RESOURCE_GROUP}' exists..."
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --tags project=community-hub environment="$ENVIRONMENT" managedBy=bicep \
  --output none

# --- Deploy ----------------------------------------------------------------

if [[ "$WHATIF_FLAG" == "--whatif" ]]; then
  echo "Running what-if (no changes will be made)..."
  az deployment group what-if \
    --resource-group "$RESOURCE_GROUP" \
    --name "$DEPLOYMENT_NAME" \
    --template-file "$TEMPLATE" \
    --parameters "@${PARAM_FILE}"
  exit 0
fi

echo "Deploying (this can take several minutes)..."
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DEPLOYMENT_NAME" \
  --template-file "$TEMPLATE" \
  --parameters "@${PARAM_FILE}" \
  --output table

# --- Show key outputs ------------------------------------------------------

echo
echo "Deployment '${DEPLOYMENT_NAME}' complete. Outputs:"
az deployment group show \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DEPLOYMENT_NAME" \
  --query properties.outputs \
  --output json

echo
echo "-----------------------------------------------------------------"
echo " NEXT STEPS (see docs/RUNBOOK.md):"
echo "   1. Store the real secret VALUES in Key Vault (Brevo, WooCommerce,"
echo "      Company Manager, the SQL admin password just used)."
echo "   2. Create the DNS CNAME for your event hostname and bind it."
echo "   3. Deploy the application code (Stage 2+) to the web + Functions apps."
echo "-----------------------------------------------------------------"
