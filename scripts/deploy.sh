#!/usr/bin/env bash
# ===========================================================================
#  deploy.sh  -  Deploy the Community Hub Azure infrastructure (Stage 1)
# ---------------------------------------------------------------------------
#  Creates the resource group and deploys infra/main.bicep into it.
#  The SQL admin password is NEVER stored in a file - it is read from the
#  ELDKHUB_SQL_ADMIN_PASSWORD environment variable, or prompted for if unset.
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
#     The target subscription defaults to the ExpertsLive Denmark sub
#     (772440e1-adf8-4fbe-82f9-bb977b55bc8b, tenant
#     7825c48b-861b-41fd-b635-ffab1aff7d13). Both dev + prod RGs live in
#     this sub -- env separation is by RG, not by subscription. Override
#     with AZURE_SUBSCRIPTION_ID:
#       AZURE_SUBSCRIPTION_ID=<other-sub-id> ./scripts/deploy.sh dev
#     The script selects the subscription explicitly, so a deploy cannot
#     land in the wrong one by accident.
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

# Target subscription. Defaults to the ExpertsLive Denmark sub (tenant
# 7825c48b-861b-41fd-b635-ffab1aff7d13). Both dev + prod RGs live in this
# sub -- env separation is by RG (rg-eldkhub-dev / rg-eldkhub-prod), not by
# subscription. Override with AZURE_SUBSCRIPTION_ID env var if needed.
AZURE_SUBSCRIPTION_ID="${AZURE_SUBSCRIPTION_ID:-772440e1-adf8-4fbe-82f9-bb977b55bc8b}"

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
# wrong one. AZURE_SUBSCRIPTION_ID defaults to the ELDK27 TEST subscription.
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

# --- SQL admin password (never stored on disk) -----------------------------

if [[ -z "${ELDKHUB_SQL_ADMIN_PASSWORD:-}" ]]; then
  echo "ELDKHUB_SQL_ADMIN_PASSWORD is not set."
  read -r -s -p "Enter the SQL administrator password for '${ENVIRONMENT}': " ELDKHUB_SQL_ADMIN_PASSWORD
  echo
  if [[ -z "$ELDKHUB_SQL_ADMIN_PASSWORD" ]]; then
    echo "ERROR: a SQL admin password is required." >&2
    exit 1
  fi
fi

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
    --parameters "@${PARAM_FILE}" \
    --parameters sqlAdminPassword="$ELDKHUB_SQL_ADMIN_PASSWORD"
  exit 0
fi

echo "Deploying (this can take several minutes)..."
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --name "$DEPLOYMENT_NAME" \
  --template-file "$TEMPLATE" \
  --parameters "@${PARAM_FILE}" \
  --parameters sqlAdminPassword="$ELDKHUB_SQL_ADMIN_PASSWORD" \
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
echo "   2. Create the DNS CNAME for hub.eldk27.expertslive.dk and bind it."
echo "   3. Deploy the application code (Stage 2+) to the web + Functions apps."
echo "-----------------------------------------------------------------"
