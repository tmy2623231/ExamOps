-- ExamOps Phase 5 structural rollback (schema 1.1.0 -> 1.0.0).
-- Run only after stopping the Phase 5 application and restoring the Phase 4 binary.
-- This script refuses to delete Phase 5 data. If releases/exports exist, roll back the
-- application code but keep these tables and schema 1.1.0 until a reviewed data-retention decision.
-- psql -v ON_ERROR_STOP=1 -d examops_db -f sql/rollback_phase5.sql

BEGIN;

DO $rollback_guard$
BEGIN
    IF EXISTS (SELECT 1 FROM result_release)
       OR EXISTS (SELECT 1 FROM result_release_item)
       OR EXISTS (SELECT 1 FROM report_export)
       OR EXISTS (SELECT 1 FROM exam_task WHERE result_release_id IS NOT NULL
                  OR result_status IN ('Published','Withdrawn')
                  OR result_withdrawn_at IS NOT NULL OR result_withdraw_reason IS NOT NULL) THEN
        RAISE EXCEPTION 'Phase5 data exists; structural rollback refused. Keep schema 1.1.0 and roll back application code only.';
    END IF;
END $rollback_guard$;

ALTER TABLE exam_task DROP CONSTRAINT IF EXISTS fk_exam_task_result_release;
ALTER TABLE exam_task DROP CONSTRAINT IF EXISTS ck_exam_task_result_release_state;
DROP INDEX IF EXISTS ix_exam_task_result_release;
ALTER TABLE exam_task DROP COLUMN IF EXISTS result_release_id;
ALTER TABLE exam_task DROP COLUMN IF EXISTS result_withdrawn_at;
ALTER TABLE exam_task DROP COLUMN IF EXISTS result_withdraw_reason;

DROP TABLE IF EXISTS report_export;
DROP TABLE IF EXISTS result_release_item;
DROP TABLE IF EXISTS result_release;

UPDATE schema_meta SET schema_version = '1.0.0', app_compat_max = '1.0.0', released_at = DATE '2026-11-24'
WHERE id = 1;

COMMIT;

\echo 'Phase5 structural rollback completed; schema 1.0.0 restored.'
