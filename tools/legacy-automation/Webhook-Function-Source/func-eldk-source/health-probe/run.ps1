using namespace System.Net

param($Request, $TriggerMetadata)

# Simple health probe for Azure Application Gateway
# Returns 200 OK so the App Gateway marks the backend as healthy

Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
    StatusCode = [HttpStatusCode]::OK
    Headers    = @{ "Content-Type" = "text/plain" }
    Body       = "OK"
})
