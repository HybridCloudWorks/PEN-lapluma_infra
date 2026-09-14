-- Blueprint Publication Lifecycle, Review Invariants, Edition Drift and Rollback
-- Delivers task INF-04: Publish immutable Blueprint revisions with independent review.

CREATE SCHEMA IF NOT EXISTS library;

-- -----------------------------------------------------------------------------
-- Blueprint Publication Audit Trail
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS library.blueprint_publication_audit
(
    audit_id        UUID         NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    namespace       VARCHAR(64)  NOT NULL,
    blueprint_id    VARCHAR(64)  NOT NULL,
    revision        INT          NOT NULL,
    action          VARCHAR(32)  NOT NULL,
    author_id       VARCHAR(256) NULL,
    reviewer_id     VARCHAR(256) NULL,
    operator_id     VARCHAR(256) NOT NULL,
    source_sha256   CHAR(64)     NULL,
    manifest_sha256 CHAR(64)     NULL,
    reason          TEXT         NULL,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT fk_audit_blueprint
        FOREIGN KEY (namespace, blueprint_id, revision)
        REFERENCES library.document_blueprint (namespace, blueprint_id, revision)
        ON DELETE CASCADE,

    CONSTRAINT ck_audit_action
        CHECK (action IN (
            'DRAFT_CREATED',
            'VALIDATED',
            'SUBMITTED_FOR_REVIEW',
            'REVIEW_APPROVED',
            'REVIEW_REJECTED',
            'PUBLISHED',
            'QUARANTINED_DRIFT',
            'WITHDRAWN',
            'ROLLED_BACK'
        ))
);

CREATE INDEX IF NOT EXISTS idx_blueprint_audit_lookup
    ON library.blueprint_publication_audit (namespace, blueprint_id, revision, created_at DESC);

-- -----------------------------------------------------------------------------
-- Function: Record Independent Review and Prevent Self-Approval
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION library.fn_review_blueprint(
    p_namespace   VARCHAR(64),
    p_blueprint_id VARCHAR(64),
    p_revision    INT,
    p_reviewer_id VARCHAR(256),
    p_author_id   VARCHAR(256),
    p_notes       TEXT DEFAULT NULL
)
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    v_current_state VARCHAR(32);
BEGIN
    SELECT publication_state INTO v_current_state
    FROM library.document_blueprint
    WHERE namespace = p_namespace AND blueprint_id = p_blueprint_id AND revision = p_revision;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Blueprint %/%@r% does not exist', p_namespace, p_blueprint_id, p_revision;
    END IF;

    -- Self-approval invariant: reviewer cannot be the author
    IF p_reviewer_id = p_author_id THEN
        RAISE EXCEPTION 'Independent review invariant violated: reviewer cannot be the author (self-approval prohibited)';
    END IF;

    -- Can only review VALIDATED drafts
    IF v_current_state NOT IN ('VALIDATED', 'IN_REVIEW') THEN
        RAISE EXCEPTION 'Cannot review blueprint in state % (must be VALIDATED or IN_REVIEW)', v_current_state;
    END IF;

    UPDATE library.document_blueprint
    SET publication_state = 'IN_REVIEW',
        reviewed_by = p_reviewer_id,
        reviewed_at = CURRENT_TIMESTAMP
    WHERE namespace = p_namespace AND blueprint_id = p_blueprint_id AND revision = p_revision;

    INSERT INTO library.blueprint_publication_audit (
        namespace, blueprint_id, revision, action,
        author_id, reviewer_id, operator_id, reason
    ) VALUES (
        p_namespace, p_blueprint_id, p_revision, 'REVIEW_APPROVED',
        p_author_id, p_reviewer_id, p_reviewer_id, p_notes
    );
END;
$$;

-- -----------------------------------------------------------------------------
-- Function: Publish Blueprint with Integrity Pinning
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION library.fn_publish_blueprint(
    p_namespace     VARCHAR(64),
    p_blueprint_id   VARCHAR(64),
    p_revision      INT,
    p_publisher_id  VARCHAR(256),
    p_manifest_sha256 CHAR(64),
    p_author_id     VARCHAR(256) DEFAULT NULL
)
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    v_current_state VARCHAR(32);
    v_reviewed_by   VARCHAR(256);
    v_source_sha    CHAR(64);
BEGIN
    SELECT publication_state, reviewed_by, source_sha256
    INTO v_current_state, v_reviewed_by, v_source_sha
    FROM library.document_blueprint
    WHERE namespace = p_namespace AND blueprint_id = p_blueprint_id AND revision = p_revision;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Blueprint %/%@r% does not exist', p_namespace, p_blueprint_id, p_revision;
    END IF;

    -- Must have completed independent review
    IF v_current_state != 'IN_REVIEW' OR v_reviewed_by IS NULL THEN
        RAISE EXCEPTION 'Cannot publish unreviewed blueprint %/%@r% (state=%, reviewed_by=%)',
            p_namespace, p_blueprint_id, p_revision, v_current_state, v_reviewed_by;
    END IF;

    -- Publisher cannot be author if author is known and reviewed_by is absent/invalid
    IF p_author_id IS NOT NULL AND v_reviewed_by = p_author_id THEN
        RAISE EXCEPTION 'Self-approval invariant violated: blueprint was self-reviewed by author %', p_author_id;
    END IF;

    -- Set previous latest to false
    UPDATE library.document_blueprint
    SET is_latest = FALSE
    WHERE namespace = p_namespace AND blueprint_id = p_blueprint_id AND is_latest = TRUE;

    -- Set this revision to PUBLISHED and latest
    UPDATE library.document_blueprint
    SET publication_state = 'PUBLISHED',
        published_at = CURRENT_TIMESTAMP,
        is_latest = TRUE
    WHERE namespace = p_namespace AND blueprint_id = p_blueprint_id AND revision = p_revision;

    INSERT INTO library.blueprint_publication_audit (
        namespace, blueprint_id, revision, action,
        author_id, reviewer_id, operator_id,
        source_sha256, manifest_sha256, reason
    ) VALUES (
        p_namespace, p_blueprint_id, p_revision, 'PUBLISHED',
        p_author_id, v_reviewed_by, p_publisher_id,
        v_source_sha, p_manifest_sha256, 'Immutable publication approved and pinned'
    );
END;
$$;

-- -----------------------------------------------------------------------------
-- Function: Source Edition Drift Quarantine
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION library.fn_quarantine_if_source_drift(
    p_namespace            VARCHAR(64),
    p_blueprint_id          VARCHAR(64),
    p_revision             INT,
    p_observed_source_sha256 CHAR(64),
    p_operator_id          VARCHAR(256),
    p_reason               TEXT DEFAULT 'Official source edition drift detected'
)
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
    v_pinned_sha    CHAR(64);
    v_current_state VARCHAR(32);
BEGIN
    SELECT source_sha256, publication_state
    INTO v_pinned_sha, v_current_state
    FROM library.document_blueprint
    WHERE namespace = p_namespace AND blueprint_id = p_blueprint_id AND revision = p_revision;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Blueprint %/%@r% does not exist', p_namespace, p_blueprint_id, p_revision;
    END IF;

    -- If observed SHA256 differs from pinned SHA256, quarantine immediately
    IF v_pinned_sha IS NOT NULL AND LOWER(v_pinned_sha) != LOWER(p_observed_source_sha256) THEN
        UPDATE library.document_blueprint
        SET publication_state = 'QUARANTINED',
            is_latest = FALSE
        WHERE namespace = p_namespace AND blueprint_id = p_blueprint_id AND revision = p_revision;

        INSERT INTO library.blueprint_publication_audit (
            namespace, blueprint_id, revision, action,
            operator_id, source_sha256, reason
        ) VALUES (
            p_namespace, p_blueprint_id, p_revision, 'QUARANTINED_DRIFT',
            p_operator_id, p_observed_source_sha256,
            format('Source drift detected: pinned=%s, observed=%s. %s', v_pinned_sha, p_observed_source_sha256, p_reason)
        );

        RETURN TRUE; -- Quarantined
    END IF;

    RETURN FALSE; -- No drift detected
END;
$$;

-- -----------------------------------------------------------------------------
-- Function: Rollback Blueprint to Prior Revision (Case Pins Unaffected)
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION library.fn_rollback_blueprint(
    p_namespace       VARCHAR(64),
    p_blueprint_id     VARCHAR(64),
    p_target_revision INT,
    p_operator_id     VARCHAR(256),
    p_reason          TEXT
)
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    v_target_state VARCHAR(32);
BEGIN
    -- Verify target revision exists and is PUBLISHED
    SELECT publication_state INTO v_target_state
    FROM library.document_blueprint
    WHERE namespace = p_namespace AND blueprint_id = p_blueprint_id AND revision = p_target_revision;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Target rollback revision %/%@r% does not exist', p_namespace, p_blueprint_id, p_target_revision;
    END IF;

    IF v_target_state != 'PUBLISHED' THEN
        RAISE EXCEPTION 'Cannot rollback to revision %/%@r% in state % (target must be PUBLISHED)',
            p_namespace, p_blueprint_id, p_target_revision, v_target_state;
    END IF;

    -- Demote current latest
    UPDATE library.document_blueprint
    SET is_latest = FALSE,
        publication_state = CASE 
            WHEN publication_state = 'PUBLISHED' THEN 'ROLLED_BACK' 
            ELSE publication_state 
        END
    WHERE namespace = p_namespace AND blueprint_id = p_blueprint_id AND is_latest = TRUE;

    -- Promote target revision to latest for all NEW cases.
    -- Historical case folders with pinned revisions remain unchanged and untouched.
    UPDATE library.document_blueprint
    SET is_latest = TRUE
    WHERE namespace = p_namespace AND blueprint_id = p_blueprint_id AND revision = p_target_revision;

    INSERT INTO library.blueprint_publication_audit (
        namespace, blueprint_id, revision, action,
        operator_id, reason
    ) VALUES (
        p_namespace, p_blueprint_id, p_target_revision, 'ROLLED_BACK',
        p_operator_id, p_reason
    );
END;
$$;
