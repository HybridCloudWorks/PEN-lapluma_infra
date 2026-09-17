# ------------------------------------------------------------------------------
# Cloud Monitoring Alert Policies (NFR-AVAIL-001, INF-19)
# ------------------------------------------------------------------------------

resource "google_monitoring_alert_policy" "cloud_run_5xx_alert" {
  display_name = "Cloud Run 5xx Errors Spike (${var.environment})"
  combiner     = "OR"

  conditions {
    display_name = "HTTP 5xx error rate elevated"

    condition_threshold {
      filter          = "resource.type = \"cloud_run_revision\" AND metric.type = \"run.googleapis.com/request_count\" AND metric.labels.response_code_class = \"5xx\""
      duration        = "300s"
      comparison      = "COMPARISON_GT"
      threshold_value = 5

      aggregations {
        alignment_period   = "60s"
        per_series_aligner = "ALIGN_RATE"
      }
    }
  }

  alert_strategy {
    auto_close = "1800s" # 30 minutes auto-resolve
  }
}

resource "google_monitoring_alert_policy" "cloud_run_memory_utilization" {
  display_name = "Cloud Run Memory Saturation Risk (${var.environment})"
  combiner     = "OR"

  conditions {
    display_name = "Memory utilization > 85%"

    condition_threshold {
      filter          = "resource.type = \"cloud_run_revision\" AND metric.type = \"run.googleapis.com/container/memory/utilizations\""
      duration        = "300s"
      comparison      = "COMPARISON_GT"
      threshold_value = 0.85

      aggregations {
        alignment_period   = "60s"
        per_series_aligner = "ALIGN_PERCENTILE_99"
      }
    }
  }

  alert_strategy {
    auto_close = "1800s"
  }
}
