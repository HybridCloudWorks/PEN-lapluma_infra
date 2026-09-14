resource "google_api_gateway_api" "api" {
  provider = google-beta
  api_id   = "lp-gateway-api-${var.environment}"
}

resource "google_api_gateway_api_config" "api_cfg" {
  provider      = google-beta
  api           = google_api_gateway_api.api.api_id
  api_config_id = "lp-cfg-${var.environment}-v1"

  openapi_documents {
    document {
      path = "spec.yaml"
      contents = base64encode(<<-EOF
openapi: "3.0.0"
info:
  title: "LaPluma API Gateway (${var.environment})"
  version: "1.0.0"
paths:
  /catalog/healthz:
    get:
      summary: "Catalog Health"
      operationId: "catalogHealth"
      x-google-backend:
        address: "${var.core_api_url}/healthz"
      responses:
        '200':
          description: "OK"
  /workflow/healthz:
    get:
      summary: "Workflow Health"
      operationId: "workflowHealth"
      x-google-backend:
        address: "${var.workflow_api_url}/healthz"
      responses:
        '200':
          description: "OK"
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
