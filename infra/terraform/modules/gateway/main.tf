resource "google_api_gateway_api" "api" {
  provider = google-beta
  api_id   = "lp-gateway-api-${var.environment}"
}

resource "google_api_gateway_api_config" "api_cfg" {
  provider      = google-beta
  api           = google_api_gateway_api.api.api_id
  api_config_id = "lp-cfg-${var.environment}-v2"

  gateway_config {
    backend_config {
      google_service_account = var.gateway_sa_email != "" ? var.gateway_sa_email : null
    }
  }

  openapi_documents {
    document {
      path = "spec.yaml"
      contents = base64encode(<<-EOF
openapi: "3.0.0"
info:
  title: "LaPluma API Gateway (${var.environment})"
  version: "1.0.0"
security:
  - lapluma_auth: []
paths:
  /catalog/healthz:
    get:
      summary: "Catalog Health"
      operationId: "catalogHealth"
      security: []
      x-google-backend:
        address: "${var.core_api_url}/healthz"
        jwt_audience: "${var.core_api_url}"
      responses:
        '200':
          description: "OK"
  /workflow/healthz:
    get:
      summary: "Workflow Health"
      operationId: "workflowHealth"
      security: []
      x-google-backend:
        address: "${var.workflow_api_url}/healthz"
        jwt_audience: "${var.workflow_api_url}"
      responses:
        '200':
          description: "OK"
  /v1/library/collections:
    get:
      summary: "Document Collections"
      operationId: "gatewayListCollections"
      x-google-backend:
        address: "${var.core_api_url}/v1/library/collections"
        jwt_audience: "${var.core_api_url}"
      responses:
        '200':
          description: "Collections"
        '401':
          description: "Unauthorized"
  /v1/cases:
    get:
      summary: "Workflow Cases"
      operationId: "gatewayListCases"
      x-google-backend:
        address: "${var.workflow_api_url}/v1/cases"
        jwt_audience: "${var.workflow_api_url}"
      responses:
        '200':
          description: "Cases"
        '401':
          description: "Unauthorized"
securityDefinitions:
  lapluma_auth:
    type: "oauth2"
    flow: "implicit"
    authorizationUrl: ""
    x-google-issuer: "${var.oidc_issuer}"
    x-google-audiences: "${var.oidc_audience}"
EOF
      )
    }
  }

  lifecycle {
    create_before_destroy = true
  }
}

resource "google_api_gateway_gateway" "gw" {
  provider   = google-beta
  api_config = google_api_gateway_api_config.api_cfg.id
  gateway_id = "lp-gateway-${var.environment}"
  region     = var.region
}
