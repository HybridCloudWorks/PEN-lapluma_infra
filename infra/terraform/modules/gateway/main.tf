resource "google_api_gateway_api" "api" {
  provider = google-beta
  api_id   = "lp-gateway-api-${var.environment}"
}

resource "google_api_gateway_api_config" "api_cfg" {
  provider             = google-beta
  api                  = google_api_gateway_api.api.api_id
  api_config_id_prefix = "lp-cfg-${var.environment}-"

  gateway_config {
    backend_config {
      google_service_account = var.gateway_sa_email != "" ? var.gateway_sa_email : null
    }
  }

  openapi_documents {
    document {
      path = "spec.yaml"
      contents = base64encode(<<-EOF
swagger: "2.0"
info:
  title: "LaPluma API Gateway (${var.environment})"
  description: "Unified public ingress for LaPluma Core and Workflow services"
  version: "1.0.0"
schemes:
  - "https"
produces:
  - "application/json"
paths:
  /catalog/health:
    get:
      summary: "Catalog Health"
      operationId: "catalogHealthLegacy"
      x-google-backend:
        address: "${var.core_api_url}/health"
        jwt_audience: "${var.core_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '200':
          description: "OK"
  /catalog/healthz:
    get:
      summary: "Catalog Health Probe"
      operationId: "catalogHealth"
      x-google-backend:
        address: "${var.core_api_url}/health"
        jwt_audience: "${var.core_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '200':
          description: "OK"
  /catalog/ready:
    get:
      summary: "Catalog Readiness Probe"
      operationId: "catalogReady"
      x-google-backend:
        address: "${var.core_api_url}/ready"
        jwt_audience: "${var.core_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '200':
          description: "Ready"
  /workflow/health:
    get:
      summary: "Workflow Health"
      operationId: "workflowHealthLegacy"
      x-google-backend:
        address: "${var.workflow_api_url}/health"
        jwt_audience: "${var.workflow_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '200':
          description: "OK"
  /workflow/healthz:
    get:
      summary: "Workflow Health Probe"
      operationId: "workflowHealth"
      x-google-backend:
        address: "${var.workflow_api_url}/health"
        jwt_audience: "${var.workflow_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '200':
          description: "OK"
  /auth/saml/metadata:
    get:
      summary: "SAML Metadata"
      operationId: "samlMetadata"
      produces:
        - "application/samlmetadata+xml"
      x-google-backend:
        address: "${var.core_api_url}/auth/saml/metadata"
        jwt_audience: "${var.core_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '200':
          description: "Metadata"
  /auth/saml/login:
    get:
      summary: "SAML Login"
      operationId: "samlLogin"
      parameters:
        - name: "domain"
          in: "query"
          required: true
          type: "string"
        - name: "provider"
          in: "query"
          required: false
          type: "string"
      x-google-backend:
        address: "${var.core_api_url}/auth/saml/login"
        jwt_audience: "${var.core_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '302':
          description: "Redirect"
  /auth/saml/acs/{tenantId}:
    post:
      summary: "SAML Assertion Consumer Service"
      operationId: "samlAcs"
      parameters:
        - name: "tenantId"
          in: "path"
          required: true
          type: "string"
      x-google-backend:
        address: "${var.core_api_url}"
        jwt_audience: "${var.core_api_url}"
        path_translation: APPEND_PATH_TO_ADDRESS
      responses:
        '200':
          description: "Token Response"
        '400':
          description: "Bad Request"
        '403':
          description: "Unauthorized"
  /v1/catalog/categories:
    get:
      summary: "Catalog Categories"
      operationId: "catalogCategories"
      x-google-backend:
        address: "${var.core_api_url}/v1/catalog/categories"
        jwt_audience: "${var.core_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '200':
          description: "Hierarchy"
  /v1/catalog/packages:
    get:
      summary: "Catalog Packages"
      operationId: "catalogPackages"
      parameters:
        - name: "categoryCode"
          in: "query"
          required: false
          type: "string"
        - name: "subcategoryCode"
          in: "query"
          required: false
          type: "string"
        - name: "activationState"
          in: "query"
          required: false
          type: "string"
      x-google-backend:
        address: "${var.core_api_url}/v1/catalog/packages"
        jwt_audience: "${var.core_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '200':
          description: "Packages"
  /v1/library/collections:
    get:
      summary: "Document Collections"
      operationId: "gatewayListCollections"
      x-google-backend:
        address: "${var.core_api_url}/v1/library/collections"
        jwt_audience: "${var.core_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '200':
          description: "Collections"
  /v1/cases:
    get:
      summary: "Workflow Cases"
      operationId: "gatewayListCases"
      x-google-backend:
        address: "${var.workflow_api_url}/v1/cases"
        jwt_audience: "${var.workflow_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '200':
          description: "Cases"
  /v1/session:
    get:
      summary: "Workflow Session Context"
      operationId: "gatewaySession"
      x-google-backend:
        address: "${var.workflow_api_url}/v1/session"
        jwt_audience: "${var.workflow_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '200':
          description: "Session"
  /v1/clients:
    get:
      summary: "Client Directory"
      operationId: "gatewayListClients"
      parameters:
        - name: "query"
          in: "query"
          required: false
          type: "string"
        - name: "cursor"
          in: "query"
          required: false
          type: "string"
      x-google-backend:
        address: "${var.workflow_api_url}/v1/clients"
        jwt_audience: "${var.workflow_api_url}"
        path_translation: CONSTANT_ADDRESS
      responses:
        '200':
          description: "Clients"
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
