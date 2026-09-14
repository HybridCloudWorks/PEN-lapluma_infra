# Official immutable form artifacts bucket
resource "google_storage_bucket" "artifacts_bucket" {
  name                        = "lp-artifacts-${var.environment}-${var.project_id}"
  location                    = var.region
  storage_class               = "STANDARD"
  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"

  versioning {
    enabled = true
  }
}

# User-uploaded documents and generated packages (evidence)
resource "google_storage_bucket" "documents_bucket" {
  name                        = "lp-documents-${var.environment}-${var.project_id}"
  location                    = var.region
  storage_class               = "STANDARD"
  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"

  versioning {
    enabled = true
  }

  soft_delete_policy {
    retention_duration_seconds = 604800 # 7 days (ratified soft-delete retention window < 30-day erasure SLA)
  }

  lifecycle_rule {
    action {
      type = "Delete"
    }
    condition {
      days_since_noncurrent_time = 7
      with_state                 = "ARCHIVED"
    }
  }

  lifecycle_rule {
    action {
      type          = "SetStorageClass"
      storage_class = "NEARLINE"
    }
    condition {
      age = 30
    }
  }
}

# Scratch bucket for temporary extraction chunks and processing output
resource "google_storage_bucket" "scratch_bucket" {
  name                        = "lp-scratch-${var.environment}-${var.project_id}"
  location                    = var.region
  storage_class               = "STANDARD"
  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"

  lifecycle_rule {
    action {
      type = "Delete"
    }
    condition {
      age = 1
    }
  }
}

# Scoped bucket IAM permissions (Zero trust)
resource "google_storage_bucket_iam_member" "core_api_artifacts_read" {
  bucket = google_storage_bucket.artifacts_bucket.name
  role   = "roles/storage.objectViewer"
  member = "serviceAccount:${var.core_api_sa_email}"
}

resource "google_storage_bucket_iam_member" "acquisition_artifacts_write" {
  bucket = google_storage_bucket.artifacts_bucket.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${var.acquisition_sa_email}"
}

resource "google_storage_bucket_iam_member" "workflow_api_documents_admin" {
  bucket = google_storage_bucket.documents_bucket.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${var.workflow_api_sa_email}"
}

resource "google_storage_bucket_iam_member" "processing_worker_documents_read" {
  bucket = google_storage_bucket.documents_bucket.name
  role   = "roles/storage.objectViewer"
  member = "serviceAccount:${var.processing_worker_sa_email}"
}

resource "google_storage_bucket_iam_member" "processing_worker_scratch_admin" {
  bucket = google_storage_bucket.scratch_bucket.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${var.processing_worker_sa_email}"
}

# Quarantine / staging bucket for direct-to-storage uploads prior to verification (INT-05 / ADR-019)
resource "google_storage_bucket" "quarantine_bucket" {
  name                        = "lp-quarantine-${var.environment}-${var.project_id}"
  location                    = var.region
  storage_class               = "STANDARD"
  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"

  lifecycle_rule {
    action {
      type = "Delete"
    }
    condition {
      age = 1 # Auto-expire abandoned or uncompleted upload sessions after 24h
    }
  }
}

resource "google_storage_bucket_iam_member" "workflow_api_quarantine_admin" {
  bucket = google_storage_bucket.quarantine_bucket.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${var.workflow_api_sa_email}"
}

resource "google_storage_bucket_iam_member" "processing_worker_quarantine_read" {
  bucket = google_storage_bucket.quarantine_bucket.name
  role   = "roles/storage.objectViewer"
  member = "serviceAccount:${var.processing_worker_sa_email}"
}
