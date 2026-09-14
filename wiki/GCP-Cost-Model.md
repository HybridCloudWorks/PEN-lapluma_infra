# LaPluma lean GCP cost model — planning estimate

Prepared 2026-09-13. USD/month. Public usage rates were checked against the linked Google pricing pages. Infrastructure amounts below are engineering budget allowances, not SKU-by-SKU quotes, billing observations or deployment validation. No trial credits, commitments or negotiated discounts are assumed.

## Selected scope and assumptions

- Cloud Run, Cloud SQL PostgreSQL only, private Cloud Storage objects, Pub/Sub, API Gateway and Scheduler. Platform-managed encryption plus TLS: no dedicated HSM or customer-managed key service in the baseline.
- Planning region us-central1; regional feature/processor availability and customer residency requirements must be validated before deployment.
- Pilot: 40 new cases/month, 50 pages/case, 100,000 production API calls/month, 100 GB retained object content, about 5 GB new logs/month. Database planning anchor: 2 vCPU/8 GiB, single-zone supervised pilot with backups; no HA claim.
- Growth: 1,000 new cases/month, 50 pages/case, 2 million production API calls/month, 1 TB retained object content, about 20 GB new logs/month. Database planning anchor: regional HA; sizing must be confirmed by measured queries and connection limits.
- Retained object quantities include a planning allowance for versions/output; they are assumptions, not derived retention-law requirements. Final lifecycle and audit retention stay governed by the approved policies.
- Separate synthetic dev and staging projects. Dev is scheduled/ephemeral (roughly 220 active database hours/month); staging uses reduced capacity, approximately 440 hours in pilot and full-month availability at growth. Storage, backups and logs continue billing when compute stops.
- Processing mix: 80% of pages use basic OCR, 20% use custom extraction. Each page uses one of these paths; do not automatically add OCR again to custom extraction. No separate model hosting, training or add-on processor charges are included.
- Text-model example only: 100,000 input plus 20,000 output/reasoning tokens per case, priced using published Gemini 2.5 Flash standard text rates. This does not select or validate that model for production. Voice, images and model escalation require a separate allowance.
- Add 10% synthetic non-production processing/token volume to production usage. Retrying/reprocessing is not included in the base; a sensitivity is shown below.

## Infrastructure allowances per month

| Component | Pilot | Growth |
|---|---:|---:|
| Cloud Run APIs, processing and jobs | $20–$50 | $50–$150 |
| PostgreSQL, storage and backups (single-zone pilot; HA growth) | $100–$130 | $220–$350 |
| Private object storage and versions | $5–$20 | $25–$60 |
| Networking, transfer and service access | $15–$50 | $40–$120 |
| Logging, monitoring and secrets | $15–$50 | $50–$140 |
| Registry, Pub/Sub and Scheduler | $5–$15 | $15–$30 |
| Infrastructure sizing allowance | $15–$35 | $0–$50 |
| **Production infrastructure subtotal** | **$175–350** | **$400–900** |
| Separate synthetic dev infrastructure | $40–100 | $40–100 |
| Separate staging infrastructure | $75–175 | $125–250 |
| **All-environment infrastructure** | **$290–625** | **$565–1,250** |

The PostgreSQL allowance is anchored by current published Cloud SQL CPU/memory pricing; it is not a complete configured instance quote. Cloud Run cost depends on billed execution time, minimum instances and concurrency. [Cloud SQL pricing](https://cloud.google.com/sql/pricing?hl=en), [Cloud Run pricing](https://cloud.google.com/run/pricing).

## Metered document and text-model usage

Published unit-rate inputs: basic OCR $1.50/1,000 pages and custom extraction $30/1,000 pages. The estimate conservatively does not apply OCR free allowances. [Document AI pricing](https://cloud.google.com/products/document-ai/pricing).

Gemini 2.5 Flash standard text rates used for the illustration: $0.30/million input tokens and $2.50/million output tokens, including reasoning. [Google model pricing](https://cloud.google.com/gemini-enterprise-agent-platform/generative-ai/pricing).

`pages = cases × 50`

`processing = pages × (0.80 × 1.50/1000 + 0.20 × 30/1000)`

`text_model = cases × (100000 × 0.30/1000000 + 20000 × 2.50/1000000)`

| Usage | Pilot | Growth |
|---|---:|---:|
| Production pages | 2000 | 50000 |
| Production document processing | $14.40 | $360.00 |
| Production text-model illustration | $3.20 | $80.00 |
| 10% non-prod processing/model allowance | $1.76 | $44.00 |
| **Illustrative total including dev/staging and stated usage** | **$309–645/month** | **$1,049–1,734/month** |

Totals before rounding are $309.36–$644.36 and $1,049–$1,734. API Gateway includes the first 2 million calls per billing account each month; the next tier is $3/million. Do not multiply the allowance across projects. At growth, an extra 10% test call volume would add roughly $0.60/month if no other account activity consumed the allowance; this fits within infrastructure sizing allowances. Network transfer is additional and budgeted separately. [API Gateway pricing](https://cloud.google.com/api-gateway/pricing).

## Sensitivities and exclusions

- If all 50,000 growth pages require custom extraction, production extraction alone is $1,500/month instead of $360. At pilot it is $60 instead of $14.40. Document mix is a larger variable than the number of catalog entries.
- A 20% repeat-processing/model workload adds approximately $3.87/month at pilot and $96.80/month at growth, on top of the stated production-plus-test usage. It does not include additional runtime and transfer.
- Fully always-on dev/staging or a high-availability pilot database raises the low-end allowances; do not promise these figures for that topology.
- Excludes engineering, customer onboarding labor, human document review, commercial support, taxes, email/SMS, voice, premium model escalation, model hosting/training, special institution contracts and any new industry-specific controls.
- No savings claim depends on unverified free-tier eligibility. Platform encryption removes key-management pool costs, not retention/deletion or access-control obligations.

## Institution and case allocation

Record opaque institution ID, environment, operation, Blueprint revision, input/output tokens, processor pages, runtime and stored/transferred bytes in content-free usage events. Keep customer text, document values and secrets out of telemetry.

`institution allocation = directly attributed usage + agreed share of environment overhead`

`allocated cost per case = institution allocation / completed billable cases`

For budgeting with the assumed new-case counts, the all-environment totals imply about $7.73–$16.11 per pilot case and $1.05–$1.73 per growth case. These are allocated planning figures, not marginal provider charges or customer prices. When no cases finish, show absolute overhead rather than dividing by zero. Account for onboarding labor separately before setting customer prices.

INF-19 must reconcile actual usage and billing after deployment, validate every selected SKU and retention assumption, and set alerts plus service-level scaling/concurrency controls. Budget alerts are not hard spending caps.
