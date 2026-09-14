#!/usr/bin/env bash
# ===========================================================================
#  grant-db-access.sh  -  Give the web app and Functions app access to the
#                         Azure SQL database, and optionally run a SQL file
# ---------------------------------------------------------------------------
#  infra/main.bicep creates an Entra-only SQL server: there is no SQL password.
#  The web app and the Functions app connect with their system-assigned managed
#  identities, which must exist as users in the database. Bicep cannot create
#  database users, so this script does it — idempotently, safe to re-run.
#
#  Usage:
#     ./scripts/grant-db-access.sh dev
#     ./scripts/grant-db-access.sh prod --staging-slot
#     ./scripts/grant-db-access.sh dev --sql-file scripts/first-organizer.sql
#
#  Options:
#     --staging-slot     also grant the web app's 'staging' deployment slot
#     --sql-file <file>  run this SQL file after the grants (e.g. the first
#                        organizer seed, once the app has created the schema)
#     --no-firewall      do not add (and remove) a temporary firewall rule
#                        for this machine's public IP
#
#  Prerequisites:
#     - az CLI, logged in (az login) as a MEMBER of the Entra group named in
#       sqlAadAdminLogin in infra/main.<env>.parameters.json
#     - AZURE_SUBSCRIPTION_ID set (as for scripts/deploy.sh)
#     - jq, curl, and go-sqlcmd (https://aka.ms/go-sqlcmd — the modern 'sqlcmd')
#     - infra deployed with scripts/deploy.sh <env>
# ===========================================================================

set -euo pipefail

ENVIRONMENT="${1:-}"
if [[ "$ENVIRONMENT" != "dev" && "$ENVIRONMENT" != "prod" ]]; then
  echo "Usage: ./scripts/grant-db-access.sh <dev|prod> [--staging-slot] [--sql-file <file>] [--no-firewall]" >&2
  exit 1
fi
shift

STAGING_SLOT=0
SQL_FILE=""
ADD_FIREWALL=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --staging-slot) STAGING_SLOT=1; shift ;;
    --sql-file)     SQL_FILE="${2:-}"; shift 2 ;;
    --no-firewall)  ADD_FIREWALL=0; shift ;;
    *) echo "ERROR: unknown option '$1'" >&2; exit 1 ;;
  esac
done

AZURE_SUBSCRIPTION_ID="${AZURE_SUBSCRIPTION_ID:-}"
if [[ -z "$AZURE_SUBSCRIPTION_ID" ]]; then
  echo "ERROR: set AZURE_SUBSCRIPTION_ID to the target subscription id." >&2
  exit 1
fi
if [[ -n "$SQL_FILE" && ! -f "$SQL_FILE" ]]; then
  echo "ERROR: SQL file not found: $SQL_FILE" >&2
  exit 1
fi
for tool in az jq curl sqlcmd; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "ERROR: '$tool' is not installed (see the prerequisites at the top of this script)." >&2
    exit 1
  fi
done

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PARAM_FILE="${SCRIPT_DIR}/../infra/main.${ENVIRONMENT}.parameters.json"
if [[ ! -f "$PARAM_FILE" ]]; then
  echo "ERROR: parameter file not found: $PARAM_FILE" >&2
  exit 1
fi
BASE_NAME="$(jq -r '.parameters.baseName.value // "communityhub"' "$PARAM_FILE")"
RESOURCE_GROUP="rg-${BASE_NAME}-${ENVIRONMENT}"
DATABASE="${BASE_NAME}-db"   # names.sqlDatabase in infra/main.bicep

# --- Resolve the deployed names from the resource group ----------------------
SQL_SERVER="$(az sql server list --subscription "$AZURE_SUBSCRIPTION_ID" -g "$RESOURCE_GROUP" --query "[0].name" -o tsv)"
SQL_FQDN="$(az sql server list --subscription "$AZURE_SUBSCRIPTION_ID" -g "$RESOURCE_GROUP" --query "[0].fullyQualifiedDomainName" -o tsv)"
WEB_APP="$(az webapp list --subscription "$AZURE_SUBSCRIPTION_ID" -g "$RESOURCE_GROUP" --query "[?!contains(kind, 'functionapp')] | [0].name" -o tsv)"
FUNCTIONS_APP="$(az functionapp list --subscription "$AZURE_SUBSCRIPTION_ID" -g "$RESOURCE_GROUP" --query "[0].name" -o tsv)"

if [[ -z "$SQL_SERVER" || -z "$WEB_APP" || -z "$FUNCTIONS_APP" ]]; then
  echo "ERROR: could not find the SQL server, web app and Functions app in '${RESOURCE_GROUP}'." >&2
  echo "       Run ./scripts/deploy.sh ${ENVIRONMENT} first, and check baseName in ${PARAM_FILE}." >&2
  exit 1
fi

echo "Resource group : ${RESOURCE_GROUP}"
echo "SQL server     : ${SQL_FQDN}"
echo "Database       : ${DATABASE}"
echo "Web app        : ${WEB_APP}"
echo "Functions app  : ${FUNCTIONS_APP}"

# --- Temporary firewall rule for this machine -----------------------------------
RULE_NAME="grant-db-access-$(date +%s)"
remove_rule() {
  echo "Removing temporary firewall rule ${RULE_NAME}..."
  az sql server firewall-rule delete --subscription "$AZURE_SUBSCRIPTION_ID" -g "$RESOURCE_GROUP" \
    -s "$SQL_SERVER" -n "$RULE_NAME" --output none || true
}
if [[ "$ADD_FIREWALL" -eq 1 ]]; then
  MY_IP="$(curl -fsS https://api.ipify.org)"
  echo "Adding temporary firewall rule ${RULE_NAME} for ${MY_IP}..."
  az sql server firewall-rule create --subscription "$AZURE_SUBSCRIPTION_ID" -g "$RESOURCE_GROUP" \
    -s "$SQL_SERVER" -n "$RULE_NAME" --start-ip-address "$MY_IP" --end-ip-address "$MY_IP" --output none
  trap remove_rule EXIT
fi

# --- Grants (idempotent) ----------------------------------------------------------
grant_sql() {
  local principal="$1"; shift
  local sql="IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'${principal}') CREATE USER [${principal}] FROM EXTERNAL PROVIDER;"
  local role
  for role in "$@"; do
    sql+=" ALTER ROLE ${role} ADD MEMBER [${principal}];"
  done
  echo "$sql"
}

GRANTS="$(grant_sql "$WEB_APP" db_datareader db_datawriter db_ddladmin)"
GRANTS+=$'\n'"$(grant_sql "$FUNCTIONS_APP" db_datareader db_datawriter)"
if [[ "$STAGING_SLOT" -eq 1 ]]; then
  GRANTS+=$'\n'"$(grant_sql "${WEB_APP}/slots/staging" db_datareader db_datawriter db_ddladmin)"
fi

echo "Granting database access..."
sqlcmd -S "$SQL_FQDN" -d "$DATABASE" --authentication-method ActiveDirectoryDefault -b -Q "$GRANTS"
echo "Database access granted."

if [[ -n "$SQL_FILE" ]]; then
  echo "Running ${SQL_FILE}..."
  sqlcmd -S "$SQL_FQDN" -d "$DATABASE" --authentication-method ActiveDirectoryDefault -b -i "$SQL_FILE"
  echo "Done."
fi
