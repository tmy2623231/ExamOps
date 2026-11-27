-- ExamOps security.sql
-- Apply after schema.sql and after creating the examops_app login role. Repeatable.
-- The application role is least-privilege; table-owner migrations are performed separately.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'examops_app') THEN
        RAISE EXCEPTION 'role examops_app must exist before security.sql';
    END IF;
END $$;

REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO examops_app;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM examops_app;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM examops_app;

-- System role definitions and canonical grants are deployment-owned and read-only to the application.
-- The application may only assign existing roles through user_role.
GRANT SELECT ON schema_meta, permission, role, role_permission TO examops_app;

-- Only identity sequences backing tables where the application may INSERT need USAGE/SELECT.
-- Read-only role/permission tables must not allow nextval calls that advance their sequences.
DO $sequence_grants$
DECLARE
    v_sequence text;
BEGIN
    FOR v_sequence IN
        SELECT c.relname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
        JOIN pg_depend d ON d.refobjid = c.oid AND d.refobjsubid = a.attnum AND d.deptype = 'i'
        WHERE n.nspname = 'public' AND c.relkind = 'r'
          AND has_table_privilege('examops_app', c.oid, 'INSERT')
    LOOP
        EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE %I TO examops_app', v_sequence);
    END LOOP;
END $sequence_grants$;

-- Mutable business data. DELETE is intentionally absent unless the API has a legitimate delete action.
GRANT SELECT, INSERT, UPDATE ON
    department, app_user, user_role, user_department,
    question_bank, question, question_version, question_option, question_tag, question_tag_link,
    question_category, question_category_link, import_batch, import_row, paper, paper_version, paper_section, paper_question,
    exam_task, exam_task_staff, assignment_batch, assignment_batch_row,
    exam_assignment, exam_attempt, attempt_answer, notification, outbox_message
TO examops_app;

-- Draft content replacement, authorization revocation and the sole permitted business delete (Draft task).
GRANT DELETE ON
    user_role, user_department, question_option, question_tag_link, question_category_link,
    paper_section, paper_question, exam_task, exam_task_staff
TO examops_app;

-- Append-only facts: SELECT/INSERT only. No UPDATE, DELETE or TRUNCATE; triggers also protect the owner.
GRANT SELECT, INSERT ON audit_event, attempt_event, score_adjustment, retake_grant, revocation_record,
    result_release, result_release_item, report_export
TO examops_app;

REVOKE CREATE ON SCHEMA public FROM examops_app;
REVOKE TRUNCATE ON ALL TABLES IN SCHEMA public FROM examops_app;

-- The login role is created by the local DBA setup script, not by the application.
