-- ExamOps schema.sql
-- PostgreSQL 17 / SQL-first authority. Safe to execute repeatedly; never drops tables or columns.
-- Conventions: lower_snake_case, bigint identity, numeric scores, three time types.
-- Legacy columns that cannot be migrated losslessly are retained and marked deprecated.

BEGIN;

CREATE TABLE IF NOT EXISTS schema_meta (
    id bigint PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    schema_version text NOT NULL UNIQUE,
    app_compat_min text NOT NULL,
    app_compat_max text NOT NULL,
    checksum text NULL,
    released_at date NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS department (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    parent_id bigint NULL REFERENCES department(id) ON DELETE RESTRICT,
    name text NOT NULL,
    path text NOT NULL UNIQUE,
    path_segment varchar(3) NOT NULL,
    sort_order integer NOT NULL DEFAULT 0,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_department_path_format CHECK (path ~ '^[0-9]{3}(\.[0-9]{3})*\.$'),
    CONSTRAINT ck_department_path_segment_format CHECK (path_segment ~ '^[0-9]{3}$'),
    CONSTRAINT ck_department_parent_not_self CHECK (parent_id IS NULL OR parent_id <> id),
    CONSTRAINT ux_department_parent_segment UNIQUE NULLS NOT DISTINCT (parent_id, path_segment)
);
CREATE INDEX IF NOT EXISTS ix_department_parent_id ON department(parent_id);
CREATE INDEX IF NOT EXISTS ix_department_active_path ON department(is_active, path);

CREATE TABLE IF NOT EXISTS app_user (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    username varchar(64) NOT NULL UNIQUE,
    display_name text NOT NULL,
    email text NULL,
    password_hash text NOT NULL DEFAULT '',
    must_change_password boolean NOT NULL DEFAULT true,
    status text NOT NULL DEFAULT 'Active',
    primary_department_id bigint NULL REFERENCES department(id) ON DELETE RESTRICT,
    last_login_at timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_app_user_status CHECK (status IN ('Active','Disabled'))
);
CREATE INDEX IF NOT EXISTS ix_app_user_primary_department_id ON app_user(primary_department_id);
CREATE INDEX IF NOT EXISTS ix_app_user_status_display_name ON app_user(status, display_name);

CREATE TABLE IF NOT EXISTS role (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code varchar(64) NOT NULL UNIQUE,
    name text NOT NULL,
    is_system boolean NOT NULL DEFAULT false,
    is_active boolean NOT NULL DEFAULT true,
    scope_type text NOT NULL DEFAULT 'DepartmentSubtree',
    created_at timestamptz NOT NULL DEFAULT now(),
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_role_scope_type CHECK (scope_type IN ('All','Department','DepartmentSubtree','Assigned','Self'))
);
-- ix_role_active_code is created after the compatibility column is added.

CREATE TABLE IF NOT EXISTS permission (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code varchar(128) NOT NULL UNIQUE,
    name text NOT NULL,
    module varchar(64) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS user_role (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id bigint NOT NULL REFERENCES app_user(id) ON DELETE CASCADE,
    role_id bigint NOT NULL REFERENCES role(id) ON DELETE RESTRICT,
    department_id bigint NULL REFERENCES department(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_user_role_scope UNIQUE NULLS NOT DISTINCT (user_id, role_id, department_id)
);
CREATE INDEX IF NOT EXISTS ix_user_role_role_id ON user_role(role_id);
CREATE INDEX IF NOT EXISTS ix_user_role_department_id ON user_role(department_id);
CREATE INDEX IF NOT EXISTS ix_user_role_user_id ON user_role(user_id);

CREATE TABLE IF NOT EXISTS role_permission (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    role_id bigint NOT NULL REFERENCES role(id) ON DELETE CASCADE,
    permission_id bigint NOT NULL REFERENCES permission(id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_role_permission_pair UNIQUE (role_id, permission_id)
);
CREATE INDEX IF NOT EXISTS ix_role_permission_permission_id ON role_permission(permission_id);
CREATE INDEX IF NOT EXISTS ix_role_permission_role_id ON role_permission(role_id);

CREATE TABLE IF NOT EXISTS user_department (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id bigint NOT NULL REFERENCES app_user(id) ON DELETE CASCADE,
    department_id bigint NOT NULL REFERENCES department(id) ON DELETE RESTRICT,
    is_primary boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_user_department_pair UNIQUE (user_id, department_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_user_department_one_primary ON user_department(user_id) WHERE is_primary;
CREATE INDEX IF NOT EXISTS ix_user_department_department_id ON user_department(department_id);
CREATE INDEX IF NOT EXISTS ix_user_department_user_id ON user_department(user_id);

CREATE TABLE IF NOT EXISTS question_bank (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name text NOT NULL,
    department_id bigint NOT NULL REFERENCES department(id) ON DELETE RESTRICT,
    is_shared boolean NOT NULL DEFAULT false,
    status text NOT NULL DEFAULT 'Active',
    created_by bigint NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_question_bank_status CHECK (status IN ('Active','Archived'))
);
CREATE INDEX IF NOT EXISTS ix_question_bank_department_id ON question_bank(department_id);

CREATE TABLE IF NOT EXISTS question (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    bank_id bigint NOT NULL REFERENCES question_bank(id) ON DELETE RESTRICT,
    external_code varchar(128) NULL,
    type text NOT NULL,
    status text NOT NULL DEFAULT 'Draft',
    current_version_id bigint NULL,
    archived_at timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_question_type CHECK (type IN ('SingleChoice','MultipleChoice','TrueFalse')),
    CONSTRAINT ck_question_status CHECK (status IN ('Draft','Published','Archived'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_question_bank_external_code ON question(bank_id, external_code) WHERE external_code IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_question_bank_id ON question(bank_id);
CREATE INDEX IF NOT EXISTS ix_question_status_type ON question(status, type);
CREATE INDEX IF NOT EXISTS ix_question_current_version_id ON question(current_version_id);

CREATE TABLE IF NOT EXISTS question_version (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    question_id bigint NOT NULL REFERENCES question(id) ON DELETE RESTRICT,
    version_no integer NOT NULL CHECK (version_no > 0),
    stem text NOT NULL,
    answer_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    difficulty text NOT NULL DEFAULT 'Medium',
    default_score numeric(6,2) NOT NULL DEFAULT 1,
    analysis text NULL,
    status text NOT NULL DEFAULT 'Draft',
    source text NOT NULL DEFAULT 'Manual',
    import_batch_id bigint NULL,
    import_row_id bigint NULL,
    published_at timestamptz NULL,
    published_by bigint NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_question_version_no UNIQUE (question_id, version_no),
    CONSTRAINT uq_question_version_owner UNIQUE (question_id, id),
    CONSTRAINT ck_question_version_status CHECK (status IN ('Draft','Published')),
    CONSTRAINT ck_question_version_difficulty CHECK (difficulty IN ('Easy','Medium','Hard')),
    CONSTRAINT ck_question_version_default_score CHECK (default_score > 0 AND default_score <= 9999.99),
    CONSTRAINT ck_question_version_source CHECK (source IN ('Manual','Import')),
    CONSTRAINT ck_question_version_import_refs CHECK (
        (source = 'Manual' AND import_batch_id IS NULL AND import_row_id IS NULL)
        OR (source = 'Import' AND import_batch_id IS NOT NULL AND import_row_id IS NOT NULL)
    )
);
CREATE INDEX IF NOT EXISTS ix_question_version_question_id ON question_version(question_id, version_no DESC);
-- Publication/import indexes are created after compatibility columns are added.

CREATE TABLE IF NOT EXISTS question_option (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    question_version_id bigint NOT NULL REFERENCES question_version(id) ON DELETE CASCADE,
    label varchar(1) NOT NULL,
    content text NOT NULL,
    is_correct boolean NOT NULL DEFAULT false,
    sort_order integer NOT NULL DEFAULT 1,
    CONSTRAINT uq_question_option_label UNIQUE (question_version_id, label),
    CONSTRAINT uq_question_option_sort UNIQUE (question_version_id, sort_order),
    CONSTRAINT ck_question_option_label CHECK (label IN ('A','B','C','D','E','F')),
    CONSTRAINT ck_question_option_sort CHECK (sort_order > 0)
);
CREATE INDEX IF NOT EXISTS ix_question_option_version_id ON question_option(question_version_id);

CREATE TABLE IF NOT EXISTS question_tag (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name varchar(128) NOT NULL UNIQUE,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS question_tag_link (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    question_version_id bigint NOT NULL REFERENCES question_version(id) ON DELETE CASCADE,
    tag_id bigint NOT NULL REFERENCES question_tag(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_question_tag_link_pair UNIQUE (question_version_id, tag_id)
);
CREATE INDEX IF NOT EXISTS ix_question_tag_link_tag_id ON question_tag_link(tag_id);
CREATE INDEX IF NOT EXISTS ix_question_tag_link_version_id ON question_tag_link(question_version_id);

CREATE TABLE IF NOT EXISTS import_batch (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    bank_id bigint NOT NULL REFERENCES question_bank(id) ON DELETE RESTRICT,
    file_name text NOT NULL,
    file_key text NOT NULL,
    file_hash char(64) NOT NULL,
    mode text NOT NULL DEFAULT 'AllOrNothing',
    status text NOT NULL DEFAULT 'Uploaded',
    total_rows integer NOT NULL DEFAULT 0,
    valid_rows integer NOT NULL DEFAULT 0,
    invalid_rows integer NOT NULL DEFAULT 0,
    warning_rows integer NOT NULL DEFAULT 0,
    duplicate_rows integer NOT NULL DEFAULT 0,
    imported_rows integer NOT NULL DEFAULT 0,
    failed_rows integer NOT NULL DEFAULT 0,
    excluded_rows integer NOT NULL DEFAULT 0,
    warning_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    confirmed_at timestamptz NULL,
    created_by bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_import_batch_hash UNIQUE (bank_id, file_hash),
    CONSTRAINT ck_import_batch_file_hash CHECK (file_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_import_batch_mode CHECK (mode IN ('AllOrNothing','SkipInvalid')),
    CONSTRAINT ck_import_batch_status_values CHECK (status IN ('Uploaded','Validating','Previewed','Importing','Completed','PartiallyFailed','Failed')),
    CONSTRAINT ck_import_batch_counts_nonnegative CHECK (
        total_rows >= 0 AND valid_rows >= 0 AND invalid_rows >= 0 AND warning_rows >= 0
        AND duplicate_rows >= 0 AND imported_rows >= 0 AND failed_rows >= 0 AND excluded_rows >= 0
        AND total_rows <= 5000 AND warning_rows <= valid_rows AND duplicate_rows <= invalid_rows
        AND imported_rows + failed_rows + excluded_rows <= total_rows
    ),
    CONSTRAINT ck_import_batch_preview_counts CHECK (
        status NOT IN ('Uploaded','Validating','Previewed') OR (valid_rows + invalid_rows = total_rows)
    ),
    CONSTRAINT ck_import_batch_terminal_counts CHECK (
        status NOT IN ('Completed','PartiallyFailed','Failed') OR (imported_rows + failed_rows + excluded_rows = total_rows)
    ),
    CONSTRAINT ck_import_batch_confirmed CHECK (
        status NOT IN ('Importing','Completed','PartiallyFailed','Failed') OR confirmed_at IS NOT NULL
    ),
    CONSTRAINT ck_import_batch_finished CHECK (
        status NOT IN ('Completed','PartiallyFailed','Failed') OR finished_at IS NOT NULL
    )
);
CREATE INDEX IF NOT EXISTS ix_import_batch_bank_created ON import_batch(bank_id, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_import_batch_created_by ON import_batch(created_by);
CREATE INDEX IF NOT EXISTS ix_import_batch_status_created ON import_batch(status, created_at DESC, id DESC);

CREATE TABLE IF NOT EXISTS import_row (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    batch_id bigint NOT NULL REFERENCES import_batch(id) ON DELETE CASCADE,
    row_no integer NOT NULL CHECK (row_no > 0),
    external_code varchar(128) NULL,
    status text NOT NULL,
    raw_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    answer_summary text NULL,
    error_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    warning_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    question_id bigint NULL REFERENCES question(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_import_row_no UNIQUE (batch_id, row_no),
    CONSTRAINT uq_import_row_owner UNIQUE (batch_id, id),
    CONSTRAINT ck_import_row_status_values CHECK (status IN ('Valid','Invalid','Warning','Imported','Excluded')),
    CONSTRAINT ck_import_row_raw_json CHECK (jsonb_typeof(raw_json) = 'object'),
    CONSTRAINT ck_import_row_error_json CHECK (jsonb_typeof(error_json) = 'array'),
    CONSTRAINT ck_import_row_warning_json CHECK (jsonb_typeof(warning_json) = 'array'),
    CONSTRAINT ck_import_row_question_only_imported CHECK (status = 'Imported' OR question_id IS NULL)
);
CREATE INDEX IF NOT EXISTS ix_import_row_batch_status ON import_row(batch_id, status, row_no);
CREATE INDEX IF NOT EXISTS ix_import_row_question_id ON import_row(question_id);
-- ix_import_row_external_code is created after the compatibility column is added.

CREATE TABLE IF NOT EXISTS question_category (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    bank_id bigint NOT NULL REFERENCES question_bank(id) ON DELETE RESTRICT,
    parent_id bigint NULL,
    name varchar(128) NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_question_category_owner UNIQUE (bank_id, id),
    CONSTRAINT ck_question_category_parent CHECK (parent_id IS NULL OR parent_id <> id),
    CONSTRAINT ck_question_category_name CHECK (length(btrim(name)) > 0),
    CONSTRAINT fk_question_category_parent_bank FOREIGN KEY (bank_id, parent_id)
        REFERENCES question_category(bank_id, id) ON DELETE RESTRICT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_question_category_root_name
    ON question_category(bank_id, name) WHERE parent_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_question_category_child_name
    ON question_category(bank_id, parent_id, name) WHERE parent_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_question_category_bank_parent ON question_category(bank_id, parent_id);

CREATE TABLE IF NOT EXISTS question_category_link (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    question_version_id bigint NOT NULL REFERENCES question_version(id) ON DELETE CASCADE,
    category_id bigint NOT NULL REFERENCES question_category(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_question_category_link_pair UNIQUE (question_version_id, category_id)
);
CREATE INDEX IF NOT EXISTS ix_question_category_link_version_id ON question_category_link(question_version_id);
CREATE INDEX IF NOT EXISTS ix_question_category_link_category_id ON question_category_link(category_id);

CREATE TABLE IF NOT EXISTS paper (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title text NOT NULL,
    department_id bigint NOT NULL REFERENCES department(id) ON DELETE RESTRICT,
    status text NOT NULL DEFAULT 'Draft',
    current_version_id bigint NULL,
    archived_at timestamptz NULL,
    created_by bigint NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_paper_status CHECK (status IN ('Draft','Published','Archived'))
);
CREATE INDEX IF NOT EXISTS ix_paper_department_id ON paper(department_id);
CREATE INDEX IF NOT EXISTS ix_paper_current_version_id ON paper(current_version_id);

CREATE TABLE IF NOT EXISTS paper_version (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    paper_id bigint NOT NULL REFERENCES paper(id) ON DELETE RESTRICT,
    version_no integer NOT NULL CHECK (version_no > 0),
    title text NOT NULL,
    total_score numeric(6,2) NOT NULL DEFAULT 0 CHECK (total_score >= 0 AND total_score <= 9999.99),
    duration_minutes integer NOT NULL DEFAULT 60,
    status text NOT NULL DEFAULT 'Draft',
    published_at timestamptz NULL,
    published_by bigint NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_paper_version_no UNIQUE (paper_id, version_no),
    CONSTRAINT uq_paper_version_owner UNIQUE (paper_id, id),
    CONSTRAINT ck_paper_version_status_values CHECK (status IN ('Draft','Published','Archived')),
    CONSTRAINT ck_paper_version_duration CHECK (duration_minutes > 0 AND duration_minutes <= 1440)
);
CREATE INDEX IF NOT EXISTS ix_paper_version_paper_id ON paper_version(paper_id, version_no DESC);
-- ix_paper_version_published_by is created after the compatibility column is added.

CREATE TABLE IF NOT EXISTS paper_section (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    paper_version_id bigint NOT NULL REFERENCES paper_version(id) ON DELETE CASCADE,
    title text NOT NULL,
    sort_order integer NOT NULL DEFAULT 1,
    selection_mode text NOT NULL DEFAULT 'Fixed',
    pick_count integer NULL,
    score_per_question numeric(6,2) NOT NULL DEFAULT 1,
    bank_id bigint NULL REFERENCES question_bank(id) ON DELETE RESTRICT,
    category_id bigint NULL,
    question_type text NULL,
    difficulty text NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_paper_section_order UNIQUE (paper_version_id, sort_order),
    CONSTRAINT uq_paper_section_owner UNIQUE (paper_version_id, id),
    CONSTRAINT ck_paper_section_mode CHECK (selection_mode IN ('Fixed','Random')),
    CONSTRAINT ck_paper_section_sort CHECK (sort_order > 0),
    CONSTRAINT ck_paper_section_pick_count CHECK (
        (selection_mode = 'Fixed' AND pick_count IS NULL)
        OR (selection_mode = 'Random' AND pick_count > 0)
    ),
    CONSTRAINT ck_paper_section_score CHECK (score_per_question > 0 AND score_per_question <= 9999.99),
    CONSTRAINT ck_paper_section_question_type CHECK (question_type IS NULL OR question_type IN ('SingleChoice','MultipleChoice','TrueFalse')),
    CONSTRAINT ck_paper_section_difficulty CHECK (difficulty IS NULL OR difficulty IN ('Easy','Medium','Hard')),
    CONSTRAINT ck_paper_section_rule_mode CHECK (
        selection_mode = 'Random'
        OR (bank_id IS NULL AND category_id IS NULL AND question_type IS NULL AND difficulty IS NULL)),
    CONSTRAINT ck_paper_section_bank_required CHECK (selection_mode <> 'Random' OR bank_id IS NOT NULL),
    CONSTRAINT fk_paper_section_category_bank FOREIGN KEY (bank_id, category_id)
        REFERENCES question_category(bank_id, id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS ix_paper_section_version_id ON paper_section(paper_version_id, sort_order);

CREATE TABLE IF NOT EXISTS paper_question (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    paper_version_id bigint NOT NULL REFERENCES paper_version(id) ON DELETE CASCADE,
    section_id bigint NOT NULL REFERENCES paper_section(id) ON DELETE CASCADE,
    question_version_id bigint NOT NULL REFERENCES question_version(id) ON DELETE RESTRICT,
    sort_order integer NOT NULL DEFAULT 1,
    score numeric(6,2) NOT NULL DEFAULT 1,
    is_pool boolean NOT NULL DEFAULT false,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_paper_question_order UNIQUE (paper_version_id, section_id, sort_order),
    CONSTRAINT uq_paper_question_member UNIQUE (paper_version_id, section_id, question_version_id),
    CONSTRAINT ck_paper_question_sort CHECK (sort_order > 0),
    CONSTRAINT ck_paper_question_score CHECK (score > 0 AND score <= 9999.99)
);
CREATE INDEX IF NOT EXISTS ix_paper_question_version_id ON paper_question(paper_version_id, section_id, sort_order);
CREATE INDEX IF NOT EXISTS ix_paper_question_section_id ON paper_question(section_id);
CREATE INDEX IF NOT EXISTS ix_paper_question_question_version_id ON paper_question(question_version_id);

CREATE TABLE IF NOT EXISTS exam_task (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    department_id bigint NOT NULL REFERENCES department(id) ON DELETE RESTRICT,
    code varchar(64) NOT NULL UNIQUE,
    title text NOT NULL,
    description text NULL,
    exam_date date NULL,
    status text NOT NULL DEFAULT 'Draft',
    paper_version_id bigint NULL REFERENCES paper_version(id) ON DELETE RESTRICT,
    available_from timestamp NOT NULL,
    available_to timestamp NOT NULL,
    duration_minutes integer NOT NULL DEFAULT 60,
    attempt_limit integer NOT NULL DEFAULT 1,
    score_policy text NOT NULL DEFAULT 'Latest',
    retake_approval text NOT NULL DEFAULT 'None',
    retake_cooldown_minutes integer NOT NULL DEFAULT 0,
    retake_deadline_at timestamptz NULL,
    pass_score numeric(6,2) NULL,
    result_release_policy text NOT NULL DEFAULT 'AfterClose',
    result_status text NOT NULL DEFAULT 'Unpublished',
    result_published_at timestamptz NULL,
    late_score_policy text NOT NULL DEFAULT 'Score',
    created_by bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    published_at timestamptz NULL,
    closed_at timestamptz NULL,
    revoked_at timestamptz NULL,
    archived_at timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ck_exam_task_status CHECK (status IN ('Draft','Published','Closed','Revoked','Archived')),
    CONSTRAINT ck_exam_task_window CHECK (available_to > available_from),
    CONSTRAINT ck_exam_task_duration CHECK (duration_minutes > 0 AND duration_minutes <= 1440),
    CONSTRAINT ck_exam_task_attempts CHECK (attempt_limit > 0),
    CONSTRAINT ck_exam_task_score_policy CHECK (score_policy IN ('Latest','Highest','First','Average')),
    CONSTRAINT ck_exam_task_approval CHECK (retake_approval IN ('None','Manager')),
    CONSTRAINT ck_exam_task_release CHECK (result_release_policy IN ('Immediate','AfterClose','Manual')),
    CONSTRAINT ck_exam_task_pass_score CHECK (pass_score IS NULL OR (pass_score >= 0 AND pass_score <= 9999.99)),
    CONSTRAINT ck_exam_task_result_status CHECK (result_status IN ('Unpublished','Published','Withdrawn')),
    CONSTRAINT ck_exam_task_late_policy CHECK (late_score_policy IN ('Score','Zero')),
    CONSTRAINT ck_exam_task_retake_cooldown CHECK (retake_cooldown_minutes >= 0),
    CONSTRAINT ck_exam_task_published_ref CHECK (status = 'Draft' OR paper_version_id IS NOT NULL),
    CONSTRAINT ck_exam_task_result_timestamp CHECK (result_status = 'Unpublished' OR result_published_at IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_exam_task_created_by ON exam_task(created_by);
CREATE INDEX IF NOT EXISTS ix_exam_task_paper_version_id ON exam_task(paper_version_id);
-- Department/status indexes are created after the compatibility column is added.

CREATE TABLE IF NOT EXISTS exam_task_staff (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    task_id bigint NOT NULL REFERENCES exam_task(id) ON DELETE CASCADE,
    user_id bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    staff_role text NOT NULL,
    assigned_by bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_exam_task_staff UNIQUE (task_id, user_id, staff_role),
    CONSTRAINT ck_exam_task_staff_role CHECK (staff_role IN ('ExamManager','Grader','Invigilator'))
);
CREATE INDEX IF NOT EXISTS ix_exam_task_staff_task_id ON exam_task_staff(task_id);
CREATE INDEX IF NOT EXISTS ix_exam_task_staff_user_id ON exam_task_staff(user_id, staff_role);
CREATE INDEX IF NOT EXISTS ix_exam_task_staff_assigned_by ON exam_task_staff(assigned_by);

CREATE TABLE IF NOT EXISTS assignment_batch (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    task_id bigint NOT NULL REFERENCES exam_task(id) ON DELETE RESTRICT,
    idempotency_key varchar(128) NOT NULL UNIQUE,
    target_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    status text NOT NULL DEFAULT 'Pending',
    total_count integer NOT NULL DEFAULT 0,
    success_count integer NOT NULL DEFAULT 0,
    failed_count integer NOT NULL DEFAULT 0,
    skipped_count integer NOT NULL DEFAULT 0,
    created_by bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    started_at timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    finished_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_assignment_batch_identity UNIQUE (id, task_id),
    CONSTRAINT ck_assignment_batch_status_values CHECK (status IN ('Pending','Running','Completed','PartiallyFailed','Failed','Revoking','Revoked')),
    CONSTRAINT ck_assignment_batch_target CHECK (jsonb_typeof(target_json) = 'object'),
    CONSTRAINT ck_assignment_batch_counts CHECK (
        total_count >= 0 AND success_count >= 0 AND failed_count >= 0 AND skipped_count >= 0
        AND success_count + failed_count + skipped_count <= total_count
    ),
    CONSTRAINT ck_assignment_batch_terminal_counts CHECK (
        status NOT IN ('Completed','PartiallyFailed','Failed','Revoked')
        OR success_count + failed_count + skipped_count = total_count
    ),
    CONSTRAINT ck_assignment_batch_started CHECK (status = 'Pending' OR started_at IS NOT NULL),
    CONSTRAINT ck_assignment_batch_finished CHECK (status NOT IN ('Completed','PartiallyFailed','Failed','Revoked') OR finished_at IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_assignment_batch_task_created ON assignment_batch(task_id, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_assignment_batch_created_by ON assignment_batch(created_by);
CREATE INDEX IF NOT EXISTS ix_assignment_batch_status_created ON assignment_batch(status, created_at DESC, id DESC);

CREATE TABLE IF NOT EXISTS assignment_batch_row (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    batch_id bigint NOT NULL REFERENCES assignment_batch(id) ON DELETE CASCADE,
    row_no integer NOT NULL,
    user_id bigint NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    target_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    status text NOT NULL DEFAULT 'Pending',
    assignment_id bigint NULL,
    error_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ux_assignment_batch_row_no UNIQUE (batch_id, row_no),
    CONSTRAINT ux_assignment_batch_row_user UNIQUE (batch_id, user_id),
    CONSTRAINT ck_assignment_batch_row_no_positive CHECK (row_no > 0),
    CONSTRAINT ck_assignment_batch_row_target CHECK (jsonb_typeof(target_json) = 'object'),
    CONSTRAINT ck_assignment_batch_row_error CHECK (jsonb_typeof(error_json) = 'array'),
    CONSTRAINT ck_assignment_batch_row_status CHECK (status IN ('Pending','Processing','Success','Skipped','Failed','Revoked')),
    CONSTRAINT ck_assignment_batch_row_result CHECK (
        (status = 'Success' AND assignment_id IS NOT NULL)
        OR (status IN ('Pending','Processing','Skipped','Failed') AND assignment_id IS NULL)
        OR status = 'Revoked'
    )
);
CREATE INDEX IF NOT EXISTS ix_assignment_batch_row_batch_status ON assignment_batch_row(batch_id, status, row_no);
CREATE INDEX IF NOT EXISTS ix_assignment_batch_row_user_id ON assignment_batch_row(user_id);
CREATE INDEX IF NOT EXISTS ix_assignment_batch_row_assignment_id ON assignment_batch_row(assignment_id);

CREATE TABLE IF NOT EXISTS exam_assignment (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    batch_id bigint NULL REFERENCES assignment_batch(id) ON DELETE RESTRICT,
    task_id bigint NOT NULL REFERENCES exam_task(id) ON DELETE RESTRICT,
    user_id bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    department_id_snapshot bigint NOT NULL REFERENCES department(id) ON DELETE RESTRICT,
    status text NOT NULL DEFAULT 'Pending',
    available_from timestamp NOT NULL,
    available_to timestamp NOT NULL,
    attempt_limit_override integer NULL,
    duration_minutes_override integer NULL,
    attempts_used integer NOT NULL DEFAULT 0,
    inherited_attempts_used integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    revoked_at timestamptz NULL,
    revoke_reason text NULL,
    reissued_from_id bigint NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_exam_assignment_identity UNIQUE (id, task_id, user_id),
    CONSTRAINT uq_exam_assignment_reissue UNIQUE (reissued_from_id, task_id, user_id),
    CONSTRAINT fk_exam_assignment_batch_owner FOREIGN KEY (batch_id, task_id)
        REFERENCES assignment_batch(id, task_id) ON DELETE RESTRICT,
    CONSTRAINT ck_exam_assignment_status CHECK (status IN ('Pending','Available','InProgress','Submitted','Graded','Closed','Expired','Revoked')),
    CONSTRAINT ck_exam_assignment_window CHECK (available_to > available_from),
    CONSTRAINT ck_exam_assignment_attempts CHECK (
        attempts_used >= 0 AND inherited_attempts_used >= 0
        AND (attempt_limit_override IS NULL OR attempt_limit_override > 0)
        AND (duration_minutes_override IS NULL OR (duration_minutes_override > 0 AND duration_minutes_override <= 1440))
    ),
    CONSTRAINT ck_exam_assignment_revoked CHECK (
        (status = 'Revoked' AND revoked_at IS NOT NULL AND char_length(revoke_reason) >= 10)
        OR (status <> 'Revoked' AND revoked_at IS NULL AND revoke_reason IS NULL)
    )
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_exam_assignment_active ON exam_assignment(task_id, user_id) WHERE status <> 'Revoked';
CREATE INDEX IF NOT EXISTS ix_exam_assignment_user_status ON exam_assignment(user_id, status);
CREATE INDEX IF NOT EXISTS ix_exam_assignment_department_status ON exam_assignment(department_id_snapshot, status);
CREATE INDEX IF NOT EXISTS ix_exam_assignment_batch_id ON exam_assignment(batch_id);
CREATE INDEX IF NOT EXISTS ix_exam_assignment_task_id ON exam_assignment(task_id);
CREATE INDEX IF NOT EXISTS ix_exam_assignment_reissued_from_id ON exam_assignment(reissued_from_id);

CREATE TABLE IF NOT EXISTS exam_attempt (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    assignment_id bigint NOT NULL REFERENCES exam_assignment(id) ON DELETE RESTRICT,
    attempt_no integer NOT NULL CHECK (attempt_no > 0),
    status text NOT NULL DEFAULT 'InProgress',
    start_key varchar(128) NOT NULL,
    submit_key varchar(128) NULL,
    paper_snapshot_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    started_at timestamptz NOT NULL DEFAULT now(),
    deadline_at timestamptz NOT NULL,
    submitted_at timestamptz NULL,
    auto_score numeric(6,2) NULL,
    score numeric(6,2) NULL,
    max_score numeric(6,2) NOT NULL DEFAULT 0,
    graded_at timestamptz NULL,
    graded_by bigint NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    is_late boolean NOT NULL DEFAULT false,
    voided_at timestamptz NULL,
    void_reason text NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_exam_attempt_no UNIQUE (assignment_id, attempt_no),
    CONSTRAINT ux_exam_attempt_start_key UNIQUE (start_key),
    CONSTRAINT ux_exam_attempt_submit_key UNIQUE (submit_key),
    CONSTRAINT ck_exam_attempt_status CHECK (status IN ('InProgress','Submitted','AutoSubmitted','Graded','Voided')),
    CONSTRAINT ck_exam_attempt_deadline CHECK (deadline_at > started_at),
    CONSTRAINT ck_exam_attempt_time_order CHECK (
        (submitted_at IS NULL OR submitted_at >= started_at)
        AND (graded_at IS NULL OR (submitted_at IS NOT NULL AND graded_at >= submitted_at))
        AND (status <> 'AutoSubmitted' OR submitted_at >= deadline_at + interval '30 seconds')
    ),
    CONSTRAINT ck_exam_attempt_score CHECK (
        max_score >= 0 AND max_score <= 9999.99
        AND (auto_score IS NULL OR (auto_score >= 0 AND auto_score <= max_score))
        AND (score IS NULL OR (score >= 0 AND score <= max_score))
    ),
    CONSTRAINT ck_exam_attempt_snapshot CHECK (
        jsonb_typeof(paper_snapshot_json) = 'object'
        AND paper_snapshot_json ? 'sections'
        AND jsonb_typeof(paper_snapshot_json -> 'sections') = 'array'
    ),
    CONSTRAINT ck_exam_attempt_submission CHECK (
        status NOT IN ('Submitted','AutoSubmitted','Graded') OR (submitted_at IS NOT NULL AND submit_key IS NOT NULL)
    ),
    CONSTRAINT ck_exam_attempt_in_progress CHECK (
        status <> 'InProgress' OR (submitted_at IS NULL AND submit_key IS NULL AND auto_score IS NULL AND score IS NULL)
    ),
    CONSTRAINT ck_exam_attempt_graded CHECK (
        status <> 'Graded' OR (graded_at IS NOT NULL AND auto_score IS NOT NULL AND score IS NOT NULL)
    ),
    CONSTRAINT ck_exam_attempt_auto_submitted CHECK (status <> 'AutoSubmitted' OR is_late),
    CONSTRAINT ck_exam_attempt_voided CHECK (status <> 'Voided' OR (voided_at IS NOT NULL AND char_length(void_reason) >= 10))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_exam_attempt_one_in_progress ON exam_attempt(assignment_id) WHERE status = 'InProgress';
CREATE INDEX IF NOT EXISTS ix_exam_attempt_status_deadline ON exam_attempt(status, deadline_at, id);
CREATE INDEX IF NOT EXISTS ix_exam_attempt_graded_by ON exam_attempt(graded_by);
CREATE INDEX IF NOT EXISTS ix_exam_attempt_assignment_no ON exam_attempt(assignment_id, attempt_no);

CREATE TABLE IF NOT EXISTS attempt_answer (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    attempt_id bigint NOT NULL REFERENCES exam_attempt(id) ON DELETE CASCADE,
    question_version_id bigint NOT NULL REFERENCES question_version(id) ON DELETE RESTRICT,
    answer_json jsonb NULL,
    score numeric(6,2) NOT NULL DEFAULT 0,
    max_score numeric(6,2) NOT NULL DEFAULT 0,
    grading_status text NOT NULL DEFAULT 'Pending',
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT uq_attempt_answer_question UNIQUE (attempt_id, question_version_id),
    CONSTRAINT ck_attempt_answer_score CHECK (score >= 0 AND max_score >= 0 AND score <= max_score AND max_score <= 9999.99),
    CONSTRAINT ck_attempt_answer_objective_score CHECK (
        (grading_status = 'Pending' AND score = 0)
        OR (grading_status = 'Graded' AND score IN (0, max_score))
        OR (grading_status = 'Invalid' AND score = 0)
    ),
    CONSTRAINT ck_attempt_answer_grading_status_values CHECK (grading_status IN ('Pending','Graded','Invalid'))
);
CREATE INDEX IF NOT EXISTS ix_attempt_answer_attempt_id ON attempt_answer(attempt_id);
CREATE INDEX IF NOT EXISTS ix_attempt_answer_question_version_id ON attempt_answer(question_version_id);
CREATE INDEX IF NOT EXISTS ix_attempt_answer_anomaly ON attempt_answer(grading_status, attempt_id) WHERE grading_status = 'Invalid';

CREATE TABLE IF NOT EXISTS attempt_event (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    attempt_id bigint NOT NULL REFERENCES exam_attempt(id) ON DELETE CASCADE,
    actor_id bigint NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    event_type text NOT NULL,
    payload_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    client_at timestamptz NULL,
    ip varchar(64) NULL,
    user_agent text NULL,
    event_key varchar(128) NOT NULL,
    CONSTRAINT ux_attempt_event_event_key UNIQUE (event_key)
);
CREATE INDEX IF NOT EXISTS ix_attempt_event_attempt_occurred ON attempt_event(attempt_id, occurred_at DESC, id DESC);
-- ix_attempt_event_actor_id is created after the compatibility column is added.
CREATE INDEX IF NOT EXISTS ix_attempt_event_type_occurred ON attempt_event(event_type, occurred_at DESC, id DESC);

CREATE TABLE IF NOT EXISTS retake_grant (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    assignment_id bigint NOT NULL REFERENCES exam_assignment(id) ON DELETE RESTRICT,
    extra_attempts integer NOT NULL,
    reason text NOT NULL,
    granted_by bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    expires_at timestamptz NULL,
    idempotency_key varchar(128) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_retake_grant_idempotency UNIQUE (idempotency_key),
    CONSTRAINT ck_retake_grant_extra CHECK (extra_attempts > 0),
    CONSTRAINT ck_retake_grant_reason CHECK (char_length(reason) > 0)
);
CREATE INDEX IF NOT EXISTS ix_retake_grant_assignment_id ON retake_grant(assignment_id);
CREATE INDEX IF NOT EXISTS ix_retake_grant_granted_by ON retake_grant(granted_by);

CREATE TABLE IF NOT EXISTS revocation_record (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    target_type text NOT NULL,
    target_id bigint NOT NULL,
    scope text NOT NULL,
    reason text NOT NULL,
    impact_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    actor_id bigint NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    actor_role text NOT NULL DEFAULT 'User',
    idempotency_key varchar(128) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_revocation_idempotency UNIQUE (idempotency_key),
    CONSTRAINT ck_revocation_target CHECK (target_type IN ('ExamTask','ExamAssignment','ExamAttempt','AssignmentBatch')),
    CONSTRAINT ck_revocation_scope CHECK (scope IN ('KeepScore','VoidScore','ForceSubmit','VoidAttempt')),
    CONSTRAINT ck_revocation_reason CHECK (char_length(reason) >= 10),
    CONSTRAINT ck_revocation_actor CHECK (actor_id IS NOT NULL OR actor_role = 'System'),
    CONSTRAINT ck_revocation_actor_role CHECK (actor_role IN ('User','System'))
);
CREATE INDEX IF NOT EXISTS ix_revocation_entity ON revocation_record(target_type, target_id, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_revocation_actor_id ON revocation_record(actor_id);

CREATE TABLE IF NOT EXISTS score_adjustment (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    attempt_id bigint NOT NULL REFERENCES exam_attempt(id) ON DELETE RESTRICT,
    old_score numeric(6,2) NOT NULL,
    new_score numeric(6,2) NOT NULL,
    reason text NOT NULL,
    adjusted_by bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    idempotency_key varchar(128) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_score_adjustment_idempotency UNIQUE (idempotency_key),
    CONSTRAINT ck_score_adjustment_range CHECK (
        old_score >= 0 AND new_score >= 0 AND old_score <= 9999.99 AND new_score <= 9999.99
    ),
    CONSTRAINT ck_score_adjustment_reason CHECK (char_length(reason) >= 10)
);
CREATE INDEX IF NOT EXISTS ix_score_adjustment_attempt_created ON score_adjustment(attempt_id, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_score_adjustment_adjusted_by ON score_adjustment(adjusted_by);

-- Phase 5: immutable result publication snapshots. Candidates and formal reports read these
-- rows only while exam_task.result_status = 'Published'; later adjustments cannot rewrite history.
CREATE TABLE IF NOT EXISTS result_release (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    task_id bigint NOT NULL REFERENCES exam_task(id) ON DELETE RESTRICT,
    release_no integer NOT NULL,
    score_policy text NOT NULL,
    pass_score numeric(6,2) NULL,
    result_release_policy text NOT NULL,
    summary_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    payload_sha256 varchar(64) NOT NULL,
    published_by bigint NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    published_at timestamptz NOT NULL DEFAULT now(),
    idempotency_key varchar(128) NOT NULL,
    CONSTRAINT ux_result_release_no UNIQUE (task_id, release_no),
    CONSTRAINT ux_result_release_idempotency UNIQUE (idempotency_key),
    CONSTRAINT ux_result_release_identity UNIQUE (id, task_id),
    CONSTRAINT ck_result_release_no CHECK (release_no > 0),
    CONSTRAINT ck_result_release_policy CHECK (score_policy IN ('Latest','Highest','First','Average')),
    CONSTRAINT ck_result_release_mode CHECK (result_release_policy IN ('Immediate','AfterClose','Manual')),
    CONSTRAINT ck_result_release_summary CHECK (jsonb_typeof(summary_json) = 'object'),
    CONSTRAINT ck_result_release_hash CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_result_release_pass_score CHECK (pass_score IS NULL OR (pass_score >= 0 AND pass_score <= 9999.99))
);
CREATE INDEX IF NOT EXISTS ix_result_release_task_published ON result_release(task_id, published_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_result_release_published_by ON result_release(published_by);

CREATE TABLE IF NOT EXISTS result_release_item (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    release_id bigint NOT NULL,
    task_id bigint NOT NULL,
    assignment_id bigint NOT NULL,
    user_id bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    department_id_snapshot bigint NOT NULL REFERENCES department(id) ON DELETE RESTRICT,
    attempt_id bigint NULL REFERENCES exam_attempt(id) ON DELETE RESTRICT,
    final_score numeric(6,2) NULL,
    max_score numeric(6,2) NULL,
    passed boolean NULL,
    source_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT ux_result_release_item_assignment UNIQUE (release_id, assignment_id),
    CONSTRAINT fk_result_release_item_release FOREIGN KEY (release_id, task_id)
        REFERENCES result_release(id, task_id) ON DELETE RESTRICT,
    CONSTRAINT fk_result_release_item_assignment FOREIGN KEY (assignment_id, task_id, user_id)
        REFERENCES exam_assignment(id, task_id, user_id) ON DELETE RESTRICT,
    CONSTRAINT ck_result_release_item_score CHECK (
        (final_score IS NULL AND max_score IS NULL AND passed IS NULL AND attempt_id IS NULL)
        OR (final_score IS NOT NULL AND max_score IS NOT NULL AND attempt_id IS NOT NULL
            AND final_score >= 0 AND final_score <= max_score AND max_score <= 9999.99)
    ),
    CONSTRAINT ck_result_release_item_source CHECK (jsonb_typeof(source_json) = 'object')
);
CREATE INDEX IF NOT EXISTS ix_result_release_item_user ON result_release_item(user_id, release_id);
CREATE INDEX IF NOT EXISTS ix_result_release_item_department ON result_release_item(department_id_snapshot, release_id);
CREATE INDEX IF NOT EXISTS ix_result_release_item_attempt ON result_release_item(attempt_id);

CREATE TABLE IF NOT EXISTS report_export (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    task_id bigint NULL REFERENCES exam_task(id) ON DELETE RESTRICT,
    report_type varchar(64) NOT NULL,
    format varchar(8) NOT NULL,
    file_key text NOT NULL UNIQUE,
    file_name text NOT NULL,
    row_count integer NOT NULL,
    filter_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_by bigint NOT NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    CONSTRAINT ck_report_export_type CHECK (report_type IN ('Summary','Distribution','PassRate','DepartmentCompare','Questions','Anomalies','Results')),
    CONSTRAINT ck_report_export_format CHECK (format IN ('CSV','XLSX')),
    CONSTRAINT ck_report_export_rows CHECK (row_count >= 0),
    CONSTRAINT ck_report_export_filter CHECK (jsonb_typeof(filter_json) = 'object'),
    CONSTRAINT ck_report_export_key CHECK (file_key !~ '^/' AND file_key !~ '^[A-Za-z]:')
);
ALTER TABLE report_export DROP CONSTRAINT IF EXISTS ck_report_export_type;
ALTER TABLE report_export ADD CONSTRAINT ck_report_export_type
    CHECK (report_type IN ('Summary','Distribution','PassRate','DepartmentCompare','Questions','Anomalies','Results'));
CREATE INDEX IF NOT EXISTS ix_report_export_creator_created ON report_export(created_by, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_report_export_task_created ON report_export(task_id, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_report_export_expires ON report_export(expires_at);

ALTER TABLE exam_task ADD COLUMN IF NOT EXISTS result_release_id bigint NULL;
ALTER TABLE exam_task ADD COLUMN IF NOT EXISTS result_withdrawn_at timestamptz NULL;
ALTER TABLE exam_task ADD COLUMN IF NOT EXISTS result_withdraw_reason text NULL;

DO $result_release_constraints$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_task'::regclass AND conname = 'fk_exam_task_result_release') THEN
        ALTER TABLE exam_task ADD CONSTRAINT fk_exam_task_result_release
            FOREIGN KEY (result_release_id, id) REFERENCES result_release(id, task_id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_task'::regclass AND conname = 'ck_exam_task_result_release_state') THEN
        ALTER TABLE exam_task ADD CONSTRAINT ck_exam_task_result_release_state CHECK (
            (result_status = 'Published' AND result_release_id IS NOT NULL AND result_published_at IS NOT NULL
                AND result_withdrawn_at IS NULL AND result_withdraw_reason IS NULL)
            OR (result_status = 'Withdrawn' AND result_release_id IS NOT NULL AND result_published_at IS NOT NULL
                AND result_withdrawn_at IS NOT NULL AND char_length(result_withdraw_reason) >= 10)
            OR (result_status = 'Unpublished' AND result_release_id IS NULL AND result_published_at IS NULL
                AND result_withdrawn_at IS NULL AND result_withdraw_reason IS NULL)
        );
    END IF;
END $result_release_constraints$;
CREATE INDEX IF NOT EXISTS ix_exam_task_result_release ON exam_task(result_release_id);

CREATE TABLE IF NOT EXISTS audit_event (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    actor_id bigint NULL REFERENCES app_user(id) ON DELETE RESTRICT,
    actor_role text NULL,
    action text NOT NULL,
    entity_type text NOT NULL,
    entity_id bigint NOT NULL,
    before_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    after_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    reason text NULL,
    request_id text NULL,
    trace_id text NULL,
    idempotency_key varchar(128) NULL,
    request_hash varchar(128) NULL,
    http_method varchar(16) NULL,
    route_template text NULL,
    result_ref_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    event_key varchar(128) NULL,
    ip varchar(64) NULL,
    user_agent text NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_audit_actor CHECK (actor_id IS NOT NULL OR actor_role = 'System'),
    CONSTRAINT ck_audit_idempotency_hash CHECK (idempotency_key IS NULL OR request_hash IS NOT NULL),
    CONSTRAINT ck_audit_result_ref CHECK (jsonb_typeof(result_ref_json) = 'object')
);
-- API idempotency and trace indexes are created after compatibility columns are added.
CREATE INDEX IF NOT EXISTS ix_audit_entity_created ON audit_event(entity_type, entity_id, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_audit_actor_created ON audit_event(actor_id, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_audit_action_created ON audit_event(action, created_at DESC, id DESC);
-- ix_audit_trace_id is created after the compatibility column is added.

CREATE TABLE IF NOT EXISTS notification (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id bigint NOT NULL REFERENCES app_user(id) ON DELETE CASCADE,
    type text NOT NULL,
    title text NOT NULL,
    body text NOT NULL,
    read_at timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_notification_user_unread ON notification(user_id, created_at DESC, id DESC) WHERE read_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_notification_user_created ON notification(user_id, created_at DESC, id DESC);

CREATE TABLE IF NOT EXISTS outbox_message (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_key varchar(128) NOT NULL,
    type text NOT NULL,
    aggregate_type text NOT NULL,
    aggregate_id bigint NOT NULL,
    payload_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    status text NOT NULL DEFAULT 'Pending',
    retry_count integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    locked_at timestamptz NULL,
    last_error text NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    sent_at timestamptz NULL,
    row_version bigint NOT NULL DEFAULT 1,
    CONSTRAINT ux_outbox_event_key UNIQUE (event_key),
    CONSTRAINT ck_outbox_status_values CHECK (status IN ('Pending','Processing','Sent','Dead')),
    CONSTRAINT ck_outbox_retry CHECK (retry_count >= 0),
    CONSTRAINT ck_outbox_sent CHECK (status <> 'Sent' OR sent_at IS NOT NULL),
    CONSTRAINT ck_outbox_payload CHECK (jsonb_typeof(payload_json) = 'object')
);
CREATE INDEX IF NOT EXISTS ix_outbox_pending ON outbox_message(status, next_attempt_at, id) WHERE status IN ('Pending','Dead');
-- Processing/aggregate indexes are created after compatibility columns are added.

-- Compatibility additions. Columns with changed semantics are added rather than rewritten in place.
ALTER TABLE department ADD COLUMN IF NOT EXISTS path_segment varchar(3) NULL;
ALTER TABLE role ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true;
ALTER TABLE role ADD COLUMN IF NOT EXISTS scope_type text NOT NULL DEFAULT 'DepartmentSubtree';
ALTER TABLE role ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
ALTER TABLE permission ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE question_bank ADD COLUMN IF NOT EXISTS created_by bigint NULL;
ALTER TABLE paper ADD COLUMN IF NOT EXISTS created_by bigint NULL;
CREATE INDEX IF NOT EXISTS ix_question_bank_created_by ON question_bank(created_by);
CREATE INDEX IF NOT EXISTS ix_paper_created_by ON paper(created_by);

DO $compat$
DECLARE
    r record;
BEGIN
    FOR r IN SELECT * FROM (VALUES
        ('role_permission', 'role_permission_pkey', 'role_id', 'permission_id'),
        ('user_department', 'user_department_pkey', 'user_id', 'department_id'),
        ('question_tag_link', 'question_tag_link_pkey', 'question_version_id', 'tag_id')
    ) AS v(table_name, constraint_name, first_column, second_column) LOOP
        IF EXISTS (
            SELECT 1 FROM pg_constraint c
            WHERE c.conrelid = r.table_name::regclass AND c.conname = r.constraint_name
              AND c.conkey = ARRAY[
                  (SELECT attnum FROM pg_attribute WHERE attrelid = r.table_name::regclass AND attname = r.first_column),
                  (SELECT attnum FROM pg_attribute WHERE attrelid = r.table_name::regclass AND attname = r.second_column)
              ]::smallint[]
        ) THEN
            EXECUTE format('ALTER TABLE %I RENAME CONSTRAINT %I TO %I', r.table_name, r.constraint_name, 'ux_' || r.table_name || '_legacy_pair');
        END IF;
    END LOOP;
END $compat$;

ALTER TABLE role_permission ADD COLUMN IF NOT EXISTS id bigint GENERATED ALWAYS AS IDENTITY;
ALTER TABLE role_permission ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE user_department ADD COLUMN IF NOT EXISTS id bigint GENERATED ALWAYS AS IDENTITY;
ALTER TABLE user_department ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE question_tag ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true;
ALTER TABLE question_tag ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE question_tag_link ADD COLUMN IF NOT EXISTS id bigint GENERATED ALWAYS AS IDENTITY;
ALTER TABLE question_tag_link ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();

DO $compat$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'role_permission'::regclass AND contype = 'p') THEN
        ALTER TABLE role_permission ADD CONSTRAINT role_permission_pkey PRIMARY KEY (id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'user_department'::regclass AND contype = 'p') THEN
        ALTER TABLE user_department ADD CONSTRAINT user_department_pkey PRIMARY KEY (id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question_tag_link'::regclass AND contype = 'p') THEN
        ALTER TABLE question_tag_link ADD CONSTRAINT question_tag_link_pkey PRIMARY KEY (id);
    END IF;
END $compat$;

UPDATE department
SET path_segment = substring(path from '([0-9]{1,3})\.$')
WHERE path_segment IS NULL AND path ~ '^[0-9]{3}(\.[0-9]{3})*\.$';
DO $compat$
BEGIN
    IF EXISTS (SELECT 1 FROM department WHERE path_segment IS NULL) THEN
        RAISE EXCEPTION 'legacy department paths must use zero-padded three-digit segments before upgrade';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'department'::regclass AND conname = 'ck_department_path_segment_format') THEN
        ALTER TABLE department ALTER COLUMN path_segment SET NOT NULL;
    END IF;
END $compat$;

ALTER TABLE question ADD COLUMN IF NOT EXISTS archived_at timestamptz NULL;
ALTER TABLE question_version ADD COLUMN IF NOT EXISTS source text NOT NULL DEFAULT 'Manual';
ALTER TABLE question_version ADD COLUMN IF NOT EXISTS import_batch_id bigint NULL;
ALTER TABLE question_version ADD COLUMN IF NOT EXISTS import_row_id bigint NULL;
ALTER TABLE question_version ADD COLUMN IF NOT EXISTS published_at timestamptz NULL;
ALTER TABLE question_version ADD COLUMN IF NOT EXISTS published_by bigint NULL;
ALTER TABLE question_version ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
ALTER TABLE import_batch ADD COLUMN IF NOT EXISTS invalid_rows integer NOT NULL DEFAULT 0;
ALTER TABLE import_batch ADD COLUMN IF NOT EXISTS warning_rows integer NOT NULL DEFAULT 0;
ALTER TABLE import_batch ADD COLUMN IF NOT EXISTS duplicate_rows integer NOT NULL DEFAULT 0;
ALTER TABLE import_batch ADD COLUMN IF NOT EXISTS excluded_rows integer NOT NULL DEFAULT 0;
ALTER TABLE import_batch ADD COLUMN IF NOT EXISTS warning_json jsonb NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE import_batch ADD COLUMN IF NOT EXISTS confirmed_at timestamptz NULL;
ALTER TABLE import_batch ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
ALTER TABLE import_batch ALTER COLUMN status SET DEFAULT 'Uploaded';
UPDATE import_batch SET invalid_rows = total_rows - valid_rows WHERE invalid_rows = 0 AND status = 'Previewed' AND valid_rows < total_rows;
ALTER TABLE import_row ADD COLUMN IF NOT EXISTS external_code varchar(128) NULL;
ALTER TABLE import_row ADD COLUMN IF NOT EXISTS answer_summary text NULL;
ALTER TABLE import_row ADD COLUMN IF NOT EXISTS warning_json jsonb NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE import_row ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE import_row ADD COLUMN IF NOT EXISTS updated_at timestamptz NULL;
ALTER TABLE import_row ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
ALTER TABLE import_row DROP CONSTRAINT IF EXISTS ck_import_row_status;
UPDATE import_row SET status = CASE status WHEN 'Error' THEN 'Invalid' WHEN 'Skipped' THEN 'Excluded' ELSE status END;
ALTER TABLE paper ADD COLUMN IF NOT EXISTS archived_at timestamptz NULL;
ALTER TABLE paper_version ADD COLUMN IF NOT EXISTS published_by bigint NULL;
ALTER TABLE paper_version ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
ALTER TABLE paper_section ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
ALTER TABLE paper_section ADD COLUMN IF NOT EXISTS bank_id bigint NULL REFERENCES question_bank(id) ON DELETE RESTRICT;
ALTER TABLE paper_section ADD COLUMN IF NOT EXISTS category_id bigint NULL REFERENCES question_category(id) ON DELETE RESTRICT;
ALTER TABLE paper_section ADD COLUMN IF NOT EXISTS question_type text NULL;
ALTER TABLE paper_section ADD COLUMN IF NOT EXISTS difficulty text NULL;
CREATE INDEX IF NOT EXISTS ix_paper_section_bank_id ON paper_section(bank_id);
CREATE INDEX IF NOT EXISTS ix_paper_section_category_id ON paper_section(category_id);
UPDATE paper_section SET score_per_question = 1 WHERE score_per_question IS NULL;
ALTER TABLE paper_section ALTER COLUMN score_per_question SET DEFAULT 1;
ALTER TABLE paper_section ALTER COLUMN score_per_question SET NOT NULL;
DO $compat$
DECLARE
    v_row record;
BEGIN
    FOR v_row IN SELECT pq.id, count(ps.id) AS section_count, min(ps.id) AS only_section
                 FROM paper_question pq
                 LEFT JOIN paper_section ps ON ps.paper_version_id = pq.paper_version_id
                 WHERE pq.section_id IS NULL
                 GROUP BY pq.id LOOP
        IF v_row.section_count <> 1 THEN
            RAISE EXCEPTION 'legacy paper_question % requires exactly one same-version section before upgrade', v_row.id;
        END IF;
        UPDATE paper_question SET section_id = v_row.only_section WHERE id = v_row.id;
    END LOOP;
    ALTER TABLE paper_question ALTER COLUMN section_id SET NOT NULL;
END $compat$;
ALTER TABLE paper_question ADD COLUMN IF NOT EXISTS is_pool boolean NOT NULL DEFAULT false;
ALTER TABLE paper_question ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
UPDATE paper_question pq SET is_pool = true
FROM paper_section ps WHERE pq.section_id = ps.id AND ps.selection_mode = 'Random';
ALTER TABLE exam_task ADD COLUMN IF NOT EXISTS department_id bigint NULL;
ALTER TABLE exam_task ADD COLUMN IF NOT EXISTS exam_date date NULL;
ALTER TABLE exam_task ADD COLUMN IF NOT EXISTS result_status text NOT NULL DEFAULT 'Unpublished';
ALTER TABLE exam_task ADD COLUMN IF NOT EXISTS result_published_at timestamptz NULL;
ALTER TABLE exam_task ADD COLUMN IF NOT EXISTS late_score_policy text NOT NULL DEFAULT 'Score';
ALTER TABLE exam_task ADD COLUMN IF NOT EXISTS archived_at timestamptz NULL;
ALTER TABLE exam_task ADD COLUMN IF NOT EXISTS retake_deadline_at timestamptz NULL;
ALTER TABLE exam_task ALTER COLUMN paper_version_id DROP NOT NULL;
UPDATE exam_task t SET department_id = p.department_id
FROM paper_version pv JOIN paper p ON p.id = pv.paper_id
WHERE t.paper_version_id = pv.id AND t.department_id IS NULL;
DO $compat$
BEGIN
    IF EXISTS (SELECT 1 FROM exam_task WHERE department_id IS NULL) THEN
        RAISE EXCEPTION 'legacy exam_task rows require a resolvable paper department before upgrade';
    END IF;
    ALTER TABLE exam_task ALTER COLUMN department_id SET NOT NULL;
END $compat$;
ALTER TABLE exam_task ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
ALTER TABLE assignment_batch ADD COLUMN IF NOT EXISTS started_at timestamptz NULL;
ALTER TABLE assignment_batch ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
ALTER TABLE assignment_batch ALTER COLUMN status SET DEFAULT 'Pending';
UPDATE assignment_batch SET started_at = created_at WHERE started_at IS NULL AND status <> 'Pending';
ALTER TABLE exam_assignment ADD COLUMN IF NOT EXISTS duration_minutes_override integer NULL;
ALTER TABLE exam_assignment ADD COLUMN IF NOT EXISTS inherited_attempts_used integer NOT NULL DEFAULT 0;
ALTER TABLE exam_assignment ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
ALTER TABLE exam_assignment ALTER COLUMN batch_id DROP NOT NULL;
ALTER TABLE exam_attempt ADD COLUMN IF NOT EXISTS start_key varchar(128) NULL;
ALTER TABLE exam_attempt ADD COLUMN IF NOT EXISTS submit_key varchar(128) NULL;
ALTER TABLE exam_attempt ADD COLUMN IF NOT EXISTS paper_snapshot_json jsonb NULL;
ALTER TABLE exam_attempt ADD COLUMN IF NOT EXISTS auto_score numeric(6,2) NULL;
ALTER TABLE exam_attempt ADD COLUMN IF NOT EXISTS is_late boolean NULL;
UPDATE exam_attempt SET start_key = 'legacy-attempt-' || id::text WHERE start_key IS NULL;
UPDATE exam_attempt SET submit_key = 'legacy-submit-' || id::text
WHERE submit_key IS NULL AND status IN ('Submitted','AutoSubmitted','Graded');
UPDATE exam_attempt SET paper_snapshot_json = jsonb_build_object(
    'paperVersionId', to_jsonb(t.paper_version_id::text), 'sections', '[]'::jsonb)
FROM exam_assignment a JOIN exam_task t ON t.id = a.task_id
WHERE exam_attempt.assignment_id = a.id AND paper_snapshot_json IS NULL;
UPDATE exam_attempt SET auto_score = score WHERE auto_score IS NULL AND status = 'Graded' AND score IS NOT NULL;
UPDATE exam_attempt SET is_late = COALESCE(submitted_at > deadline_at, false) WHERE is_late IS NULL;
ALTER TABLE exam_attempt ALTER COLUMN start_key SET NOT NULL;
ALTER TABLE exam_attempt ALTER COLUMN paper_snapshot_json SET DEFAULT '{}'::jsonb;
ALTER TABLE exam_attempt ALTER COLUMN paper_snapshot_json SET NOT NULL;
ALTER TABLE exam_attempt ALTER COLUMN is_late SET DEFAULT false;
ALTER TABLE exam_attempt ALTER COLUMN is_late SET NOT NULL;
ALTER TABLE exam_attempt ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
ALTER TABLE attempt_answer ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE attempt_answer ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
ALTER TABLE attempt_answer ALTER COLUMN answer_json DROP NOT NULL;
ALTER TABLE attempt_answer ALTER COLUMN answer_json DROP DEFAULT;
ALTER TABLE attempt_answer ALTER COLUMN grading_status SET DEFAULT 'Pending';
ALTER TABLE attempt_answer DROP CONSTRAINT IF EXISTS ck_attempt_answer_grading_status;
UPDATE attempt_answer SET grading_status = CASE grading_status WHEN 'AutoGraded' THEN 'Graded' WHEN 'FormatError' THEN 'Invalid' ELSE grading_status END;
DROP INDEX IF EXISTS ix_attempt_answer_anomaly;
CREATE INDEX IF NOT EXISTS ix_attempt_answer_anomaly ON attempt_answer(grading_status, attempt_id) WHERE grading_status = 'Invalid';
ALTER TABLE attempt_event ADD COLUMN IF NOT EXISTS actor_id bigint NULL;
ALTER TABLE attempt_event ADD COLUMN IF NOT EXISTS event_key varchar(128) NULL;
UPDATE attempt_event SET event_key = 'legacy-event-' || id::text WHERE event_key IS NULL;
ALTER TABLE attempt_event ALTER COLUMN event_key SET NOT NULL;
ALTER TABLE retake_grant ADD COLUMN IF NOT EXISTS idempotency_key varchar(128) NULL;
UPDATE retake_grant SET idempotency_key = 'legacy-retake-' || id::text WHERE idempotency_key IS NULL;
ALTER TABLE retake_grant ALTER COLUMN idempotency_key SET NOT NULL;
ALTER TABLE revocation_record ADD COLUMN IF NOT EXISTS actor_role text NOT NULL DEFAULT 'User';
ALTER TABLE revocation_record ADD COLUMN IF NOT EXISTS idempotency_key varchar(128) NULL;
UPDATE revocation_record SET idempotency_key = 'legacy-revocation-' || id::text WHERE idempotency_key IS NULL;
ALTER TABLE revocation_record ALTER COLUMN idempotency_key SET NOT NULL;
ALTER TABLE score_adjustment ADD COLUMN IF NOT EXISTS idempotency_key varchar(128) NULL;
UPDATE score_adjustment SET idempotency_key = 'legacy-adjustment-' || id::text WHERE idempotency_key IS NULL;
ALTER TABLE score_adjustment ALTER COLUMN idempotency_key SET NOT NULL;
ALTER TABLE audit_event ADD COLUMN IF NOT EXISTS trace_id text NULL;
ALTER TABLE audit_event ADD COLUMN IF NOT EXISTS idempotency_key varchar(128) NULL;
ALTER TABLE audit_event ADD COLUMN IF NOT EXISTS request_hash varchar(128) NULL;
ALTER TABLE audit_event ADD COLUMN IF NOT EXISTS http_method varchar(16) NULL;
ALTER TABLE audit_event ADD COLUMN IF NOT EXISTS route_template text NULL;
ALTER TABLE audit_event ADD COLUMN IF NOT EXISTS result_ref_json jsonb NOT NULL DEFAULT '{}'::jsonb;
ALTER TABLE audit_event ADD COLUMN IF NOT EXISTS event_key varchar(128) NULL;
ALTER TABLE outbox_message ADD COLUMN IF NOT EXISTS event_key varchar(128) NULL;
ALTER TABLE outbox_message ADD COLUMN IF NOT EXISTS aggregate_type text NULL;
ALTER TABLE outbox_message ADD COLUMN IF NOT EXISTS aggregate_id bigint NULL;
ALTER TABLE outbox_message ADD COLUMN IF NOT EXISTS locked_at timestamptz NULL;
ALTER TABLE outbox_message ADD COLUMN IF NOT EXISTS sent_at timestamptz NULL;
ALTER TABLE outbox_message ADD COLUMN IF NOT EXISTS row_version bigint NOT NULL DEFAULT 1;
UPDATE outbox_message SET event_key = 'legacy-outbox-' || id::text WHERE event_key IS NULL;
UPDATE outbox_message SET aggregate_type = 'Legacy', aggregate_id = id WHERE aggregate_type IS NULL OR aggregate_id IS NULL;
ALTER TABLE outbox_message DROP CONSTRAINT IF EXISTS ck_outbox_status;
UPDATE outbox_message SET status = CASE status WHEN 'Completed' THEN 'Sent' WHEN 'Failed' THEN 'Dead' ELSE status END;
DO $compat$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'outbox_message'::regclass
               AND attname = 'completed_at' AND NOT attisdropped) THEN
        UPDATE outbox_message SET sent_at = COALESCE(sent_at, completed_at) WHERE status = 'Sent' AND sent_at IS NULL;
    END IF;
END $compat$;
ALTER TABLE outbox_message ALTER COLUMN event_key SET NOT NULL;
ALTER TABLE outbox_message ALTER COLUMN aggregate_type SET NOT NULL;
ALTER TABLE outbox_message ALTER COLUMN aggregate_id SET NOT NULL;

-- Indexes that depend on compatibility columns must be created only after those columns exist.
CREATE INDEX IF NOT EXISTS ix_role_active_code ON role(is_active, code);
CREATE INDEX IF NOT EXISTS ix_question_version_published_by ON question_version(published_by);
CREATE INDEX IF NOT EXISTS ix_question_version_import_batch_id ON question_version(import_batch_id);
CREATE INDEX IF NOT EXISTS ix_question_version_import_row_id ON question_version(import_row_id);
CREATE INDEX IF NOT EXISTS ix_import_row_external_code ON import_row(batch_id, external_code);
CREATE INDEX IF NOT EXISTS ix_paper_version_published_by ON paper_version(published_by);
CREATE INDEX IF NOT EXISTS ix_exam_task_department_status ON exam_task(department_id, status, available_from, available_to);
CREATE INDEX IF NOT EXISTS ix_exam_task_status_window ON exam_task(status, available_from, available_to);
CREATE INDEX IF NOT EXISTS ix_attempt_event_actor_id ON attempt_event(actor_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_audit_api_idempotency
    ON audit_event(actor_id, http_method, route_template, idempotency_key)
    WHERE actor_id IS NOT NULL AND http_method IS NOT NULL AND route_template IS NOT NULL AND idempotency_key IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_audit_event_key ON audit_event(event_key) WHERE event_key IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_audit_trace_id ON audit_event(trace_id, created_at DESC, id DESC) WHERE trace_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_outbox_processing ON outbox_message(status, locked_at, id) WHERE status = 'Processing';
CREATE INDEX IF NOT EXISTS ix_outbox_aggregate ON outbox_message(aggregate_type, aggregate_id, created_at DESC, id DESC);

-- All concurrency tokens start at 1. Table names below are a fixed whitelist.
DO $compat$
DECLARE
    v_table text;
BEGIN
    FOREACH v_table IN ARRAY ARRAY[
        'department','app_user','role','question_bank','question','question_version','question_category','import_batch','import_row',
        'paper','paper_version','paper_section','paper_question','exam_task','assignment_batch','assignment_batch_row',
        'exam_assignment','exam_attempt','attempt_answer','outbox_message'
    ] LOOP
        EXECUTE format('ALTER TABLE %I ALTER COLUMN row_version SET DEFAULT 1', v_table);
        EXECUTE format('UPDATE %I SET row_version = 1 WHERE row_version = 0', v_table);
    END LOOP;
END $compat$;

-- Replace legacy CHECKs whose value sets or semantics changed. Canonical names are stable.
ALTER TABLE question_version DROP CONSTRAINT IF EXISTS ck_question_version_score;
ALTER TABLE import_batch DROP CONSTRAINT IF EXISTS ck_import_batch_status;
ALTER TABLE import_row DROP CONSTRAINT IF EXISTS ck_import_row_status;
ALTER TABLE paper_version DROP CONSTRAINT IF EXISTS ck_paper_version_status;
ALTER TABLE assignment_batch DROP CONSTRAINT IF EXISTS ck_assignment_batch_status;
ALTER TABLE attempt_answer DROP CONSTRAINT IF EXISTS ck_attempt_answer_grading_status;
ALTER TABLE outbox_message DROP CONSTRAINT IF EXISTS ck_outbox_status;
ALTER TABLE exam_attempt DROP CONSTRAINT IF EXISTS ck_exam_attempt_score;

-- Named constraints for databases created by the previous schema revision.
DO $constraints$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'department'::regclass AND conname = 'ck_department_path_format') THEN
        ALTER TABLE department ADD CONSTRAINT ck_department_path_format CHECK (path ~ '^[0-9]{3}(\.[0-9]{3})*\.$');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'department'::regclass AND conname = 'ck_department_path_segment_format') THEN
        ALTER TABLE department ADD CONSTRAINT ck_department_path_segment_format CHECK (path_segment ~ '^[0-9]{3}$');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'department'::regclass AND conname = 'ux_department_parent_segment') THEN
        ALTER TABLE department ADD CONSTRAINT ux_department_parent_segment UNIQUE NULLS NOT DISTINCT (parent_id, path_segment);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'role'::regclass AND conname = 'ck_role_scope_type') THEN
        ALTER TABLE role ADD CONSTRAINT ck_role_scope_type CHECK (scope_type IN ('All','Department','DepartmentSubtree','Assigned','Self'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question_bank'::regclass AND conname = 'fk_question_bank_created_by') THEN
        ALTER TABLE question_bank ADD CONSTRAINT fk_question_bank_created_by
            FOREIGN KEY (created_by) REFERENCES app_user(id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper'::regclass AND conname = 'fk_paper_created_by') THEN
        ALTER TABLE paper ADD CONSTRAINT fk_paper_created_by
            FOREIGN KEY (created_by) REFERENCES app_user(id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'assignment_batch_row'::regclass AND conname = 'fk_assignment_batch_row_assignment') THEN
        ALTER TABLE assignment_batch_row ADD CONSTRAINT fk_assignment_batch_row_assignment
            FOREIGN KEY (assignment_id) REFERENCES exam_assignment(id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'role_permission'::regclass AND conname = 'ux_role_permission_pair') THEN
        ALTER TABLE role_permission ADD CONSTRAINT ux_role_permission_pair UNIQUE (role_id, permission_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'user_role'::regclass AND conname = 'ux_user_role_scope') THEN
        IF EXISTS (SELECT 1 FROM pg_index WHERE indexrelid = 'public.ux_user_role_scope'::regclass
                   AND indrelid = 'user_role'::regclass AND indisunique) THEN
            ALTER TABLE user_role ADD CONSTRAINT ux_user_role_scope UNIQUE USING INDEX ux_user_role_scope;
        ELSE
            ALTER TABLE user_role ADD CONSTRAINT ux_user_role_scope UNIQUE NULLS NOT DISTINCT (user_id, role_id, department_id);
        END IF;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'user_department'::regclass AND conname = 'ux_user_department_pair') THEN
        ALTER TABLE user_department ADD CONSTRAINT ux_user_department_pair UNIQUE (user_id, department_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question_tag_link'::regclass AND conname = 'ux_question_tag_link_pair') THEN
        ALTER TABLE question_tag_link ADD CONSTRAINT ux_question_tag_link_pair UNIQUE (question_version_id, tag_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question_version'::regclass AND conname = 'uq_question_version_owner') THEN
        ALTER TABLE question_version ADD CONSTRAINT uq_question_version_owner UNIQUE (question_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question'::regclass AND conname = 'fk_question_current_version_owner') THEN
        ALTER TABLE question ADD CONSTRAINT fk_question_current_version_owner
            FOREIGN KEY (id, current_version_id) REFERENCES question_version(question_id, id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question_version'::regclass AND conname = 'ck_question_version_default_score') THEN
        ALTER TABLE question_version ADD CONSTRAINT ck_question_version_default_score CHECK (default_score > 0 AND default_score <= 9999.99);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question_version'::regclass AND conname = 'ck_question_version_source') THEN
        ALTER TABLE question_version ADD CONSTRAINT ck_question_version_source CHECK (source IN ('Manual','Import'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question_version'::regclass AND conname = 'ck_question_version_import_refs') THEN
        ALTER TABLE question_version ADD CONSTRAINT ck_question_version_import_refs CHECK (
            (source = 'Manual' AND import_batch_id IS NULL AND import_row_id IS NULL)
            OR (source = 'Import' AND import_batch_id IS NOT NULL AND import_row_id IS NOT NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question_version'::regclass AND conname = 'fk_question_version_import_batch') THEN
        ALTER TABLE question_version ADD CONSTRAINT fk_question_version_import_batch
            FOREIGN KEY (import_batch_id) REFERENCES import_batch(id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question_version'::regclass AND conname = 'fk_question_version_import_row') THEN
        ALTER TABLE question_version ADD CONSTRAINT fk_question_version_import_row
            FOREIGN KEY (import_row_id) REFERENCES import_row(id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'import_row'::regclass AND conname = 'uq_import_row_owner') THEN
        ALTER TABLE import_row ADD CONSTRAINT uq_import_row_owner UNIQUE (batch_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question_version'::regclass AND conname = 'fk_question_version_import_owner') THEN
        ALTER TABLE question_version ADD CONSTRAINT fk_question_version_import_owner
            FOREIGN KEY (import_batch_id, import_row_id) REFERENCES import_row(batch_id, id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question_version'::regclass AND conname = 'fk_question_version_published_by') THEN
        ALTER TABLE question_version ADD CONSTRAINT fk_question_version_published_by
            FOREIGN KEY (published_by) REFERENCES app_user(id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'question_option'::regclass AND conname = 'uq_question_option_sort') THEN
        ALTER TABLE question_option ADD CONSTRAINT uq_question_option_sort UNIQUE (question_version_id, sort_order);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'import_batch'::regclass AND conname = 'ck_import_batch_status_values') THEN
        ALTER TABLE import_batch ADD CONSTRAINT ck_import_batch_status_values CHECK (status IN ('Uploaded','Validating','Previewed','Importing','Completed','PartiallyFailed','Failed'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'import_batch'::regclass AND conname = 'ck_import_batch_counts_nonnegative') THEN
        ALTER TABLE import_batch ADD CONSTRAINT ck_import_batch_counts_nonnegative CHECK (
            total_rows >= 0 AND valid_rows >= 0 AND invalid_rows >= 0 AND warning_rows >= 0 AND duplicate_rows >= 0
            AND imported_rows >= 0 AND failed_rows >= 0 AND excluded_rows >= 0 AND total_rows <= 5000
            AND warning_rows <= valid_rows AND duplicate_rows <= invalid_rows
            AND imported_rows + failed_rows + excluded_rows <= total_rows);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'import_batch'::regclass AND conname = 'ck_import_batch_preview_counts') THEN
        ALTER TABLE import_batch ADD CONSTRAINT ck_import_batch_preview_counts CHECK (
            status NOT IN ('Uploaded','Validating','Previewed') OR valid_rows + invalid_rows = total_rows);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'import_batch'::regclass AND conname = 'ck_import_batch_terminal_counts') THEN
        ALTER TABLE import_batch ADD CONSTRAINT ck_import_batch_terminal_counts CHECK (
            status NOT IN ('Completed','PartiallyFailed','Failed') OR imported_rows + failed_rows + excluded_rows = total_rows);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'import_row'::regclass AND conname = 'ck_import_row_status_values') THEN
        ALTER TABLE import_row ADD CONSTRAINT ck_import_row_status_values CHECK (status IN ('Valid','Invalid','Warning','Imported','Excluded'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'import_row'::regclass AND conname = 'ck_import_row_question_only_imported') THEN
        ALTER TABLE import_row ADD CONSTRAINT ck_import_row_question_only_imported CHECK (status = 'Imported' OR question_id IS NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_version'::regclass AND conname = 'uq_paper_version_owner') THEN
        ALTER TABLE paper_version ADD CONSTRAINT uq_paper_version_owner UNIQUE (paper_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper'::regclass AND conname = 'fk_paper_current_version_owner') THEN
        ALTER TABLE paper ADD CONSTRAINT fk_paper_current_version_owner
            FOREIGN KEY (id, current_version_id) REFERENCES paper_version(paper_id, id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_version'::regclass AND conname = 'ck_paper_version_status_values') THEN
        ALTER TABLE paper_version ADD CONSTRAINT ck_paper_version_status_values CHECK (status IN ('Draft','Published','Archived'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_version'::regclass AND conname = 'fk_paper_version_published_by') THEN
        ALTER TABLE paper_version ADD CONSTRAINT fk_paper_version_published_by
            FOREIGN KEY (published_by) REFERENCES app_user(id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_section'::regclass AND conname = 'uq_paper_section_order') THEN
        ALTER TABLE paper_section ADD CONSTRAINT uq_paper_section_order UNIQUE (paper_version_id, sort_order);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_section'::regclass AND conname = 'uq_paper_section_owner') THEN
        ALTER TABLE paper_section ADD CONSTRAINT uq_paper_section_owner UNIQUE (paper_version_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_section'::regclass AND conname = 'ck_paper_section_pick_count') THEN
        ALTER TABLE paper_section ADD CONSTRAINT ck_paper_section_pick_count CHECK (
            (selection_mode = 'Fixed' AND pick_count IS NULL) OR (selection_mode = 'Random' AND pick_count > 0));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_question'::regclass AND conname = 'fk_paper_question_section_owner') THEN
        ALTER TABLE paper_question ADD CONSTRAINT fk_paper_question_section_owner
            FOREIGN KEY (paper_version_id, section_id) REFERENCES paper_section(paper_version_id, id) ON DELETE CASCADE;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_question'::regclass AND conname = 'uq_paper_question_order') THEN
        ALTER TABLE paper_question ADD CONSTRAINT uq_paper_question_order UNIQUE (paper_version_id, section_id, sort_order);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_task'::regclass AND conname = 'fk_exam_task_department') THEN
        ALTER TABLE exam_task ADD CONSTRAINT fk_exam_task_department
            FOREIGN KEY (department_id) REFERENCES department(id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_task'::regclass AND conname = 'ck_exam_task_result_status') THEN
        ALTER TABLE exam_task ADD CONSTRAINT ck_exam_task_result_status CHECK (result_status IN ('Unpublished','Published','Withdrawn'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_task'::regclass AND conname = 'ck_exam_task_late_policy') THEN
        ALTER TABLE exam_task ADD CONSTRAINT ck_exam_task_late_policy CHECK (late_score_policy IN ('Score','Zero'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_task'::regclass AND conname = 'ck_exam_task_published_ref') THEN
        ALTER TABLE exam_task ADD CONSTRAINT ck_exam_task_published_ref CHECK (status = 'Draft' OR paper_version_id IS NOT NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_task'::regclass AND conname = 'ck_exam_task_result_timestamp') THEN
        ALTER TABLE exam_task ADD CONSTRAINT ck_exam_task_result_timestamp CHECK (result_status = 'Unpublished' OR result_published_at IS NOT NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'assignment_batch'::regclass AND conname = 'ck_assignment_batch_status_values') THEN
        ALTER TABLE assignment_batch ADD CONSTRAINT ck_assignment_batch_status_values CHECK (status IN ('Pending','Running','Completed','PartiallyFailed','Failed','Revoking','Revoked'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'assignment_batch'::regclass AND conname = 'ck_assignment_batch_counts') THEN
        ALTER TABLE assignment_batch ADD CONSTRAINT ck_assignment_batch_counts CHECK (
            total_count >= 0 AND success_count >= 0 AND failed_count >= 0 AND skipped_count >= 0
            AND success_count + failed_count + skipped_count <= total_count);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'assignment_batch'::regclass AND conname = 'ck_assignment_batch_terminal_counts') THEN
        ALTER TABLE assignment_batch ADD CONSTRAINT ck_assignment_batch_terminal_counts CHECK (
            status NOT IN ('Completed','PartiallyFailed','Failed','Revoked') OR success_count + failed_count + skipped_count = total_count);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'assignment_batch'::regclass AND conname = 'ck_assignment_batch_started') THEN
        ALTER TABLE assignment_batch ADD CONSTRAINT ck_assignment_batch_started CHECK (status = 'Pending' OR started_at IS NOT NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'assignment_batch'::regclass AND conname = 'ck_assignment_batch_finished') THEN
        ALTER TABLE assignment_batch ADD CONSTRAINT ck_assignment_batch_finished CHECK (
            status NOT IN ('Completed','PartiallyFailed','Failed','Revoked') OR finished_at IS NOT NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_assignment'::regclass AND conname = 'uq_exam_assignment_reissue') THEN
        ALTER TABLE exam_assignment ADD CONSTRAINT uq_exam_assignment_reissue UNIQUE (reissued_from_id, task_id, user_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_assignment'::regclass AND conname = 'uq_exam_assignment_identity') THEN
        ALTER TABLE exam_assignment ADD CONSTRAINT uq_exam_assignment_identity UNIQUE (id, task_id, user_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_assignment'::regclass AND conname = 'fk_exam_assignment_reissued_from') THEN
        ALTER TABLE exam_assignment ADD CONSTRAINT fk_exam_assignment_reissued_from
            FOREIGN KEY (reissued_from_id, task_id, user_id) REFERENCES exam_assignment(id, task_id, user_id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_assignment'::regclass AND conname = 'ck_exam_assignment_revoked') THEN
        ALTER TABLE exam_assignment ADD CONSTRAINT ck_exam_assignment_revoked CHECK (
            status <> 'Revoked' OR (revoked_at IS NOT NULL AND char_length(revoke_reason) >= 10));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_attempt'::regclass AND conname = 'ux_exam_attempt_start_key') THEN
        ALTER TABLE exam_attempt ADD CONSTRAINT ux_exam_attempt_start_key UNIQUE (start_key);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_attempt'::regclass AND conname = 'ux_exam_attempt_submit_key') THEN
        ALTER TABLE exam_attempt ADD CONSTRAINT ux_exam_attempt_submit_key UNIQUE (submit_key);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_attempt'::regclass AND conname = 'ck_exam_attempt_score') THEN
        ALTER TABLE exam_attempt ADD CONSTRAINT ck_exam_attempt_score CHECK (
            max_score >= 0 AND max_score <= 9999.99
            AND (auto_score IS NULL OR (auto_score >= 0 AND auto_score <= max_score))
            AND (score IS NULL OR (score >= 0 AND score <= max_score)));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_attempt'::regclass AND conname = 'ck_exam_attempt_snapshot') THEN
        ALTER TABLE exam_attempt ADD CONSTRAINT ck_exam_attempt_snapshot CHECK (
            jsonb_typeof(paper_snapshot_json) = 'object' AND jsonb_typeof(paper_snapshot_json -> 'sections') = 'array');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'attempt_answer'::regclass AND conname = 'ck_attempt_answer_grading_status_values') THEN
        ALTER TABLE attempt_answer ADD CONSTRAINT ck_attempt_answer_grading_status_values CHECK (grading_status IN ('Pending','Graded','Invalid'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'attempt_answer'::regclass AND conname = 'ck_attempt_answer_objective_score') THEN
        ALTER TABLE attempt_answer ADD CONSTRAINT ck_attempt_answer_objective_score CHECK (
            (grading_status = 'Pending' AND score = 0)
            OR (grading_status = 'Graded' AND score IN (0, max_score))
            OR (grading_status = 'Invalid' AND score = 0));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'attempt_event'::regclass AND conname = 'ux_attempt_event_event_key') THEN
        ALTER TABLE attempt_event ADD CONSTRAINT ux_attempt_event_event_key UNIQUE (event_key);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'attempt_event'::regclass AND conname = 'fk_attempt_event_actor') THEN
        ALTER TABLE attempt_event ADD CONSTRAINT fk_attempt_event_actor FOREIGN KEY (actor_id) REFERENCES app_user(id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'retake_grant'::regclass AND conname = 'ux_retake_grant_idempotency') THEN
        ALTER TABLE retake_grant ADD CONSTRAINT ux_retake_grant_idempotency UNIQUE (idempotency_key);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'revocation_record'::regclass AND conname = 'ux_revocation_idempotency') THEN
        ALTER TABLE revocation_record ADD CONSTRAINT ux_revocation_idempotency UNIQUE (idempotency_key);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'score_adjustment'::regclass AND conname = 'ux_score_adjustment_idempotency') THEN
        ALTER TABLE score_adjustment ADD CONSTRAINT ux_score_adjustment_idempotency UNIQUE (idempotency_key);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'revocation_record'::regclass AND conname = 'ck_revocation_reason') THEN
        ALTER TABLE revocation_record ADD CONSTRAINT ck_revocation_reason CHECK (char_length(reason) >= 10);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'score_adjustment'::regclass AND conname = 'ck_score_adjustment_reason') THEN
        ALTER TABLE score_adjustment ADD CONSTRAINT ck_score_adjustment_reason CHECK (char_length(reason) >= 10);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'outbox_message'::regclass AND conname = 'ck_outbox_status_values') THEN
        ALTER TABLE outbox_message ADD CONSTRAINT ck_outbox_status_values CHECK (status IN ('Pending','Processing','Sent','Dead'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'outbox_message'::regclass AND conname = 'ux_outbox_event_key') THEN
        ALTER TABLE outbox_message ADD CONSTRAINT ux_outbox_event_key UNIQUE (event_key);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'import_batch'::regclass AND conname = 'ck_import_batch_confirmed') THEN
        ALTER TABLE import_batch ADD CONSTRAINT ck_import_batch_confirmed CHECK (
            status NOT IN ('Importing','Completed','PartiallyFailed','Failed') OR confirmed_at IS NOT NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'import_batch'::regclass AND conname = 'ck_import_batch_finished') THEN
        ALTER TABLE import_batch ADD CONSTRAINT ck_import_batch_finished CHECK (
            status NOT IN ('Completed','PartiallyFailed','Failed') OR finished_at IS NOT NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_section'::regclass AND conname = 'ck_paper_section_sort') THEN
        ALTER TABLE paper_section ADD CONSTRAINT ck_paper_section_sort CHECK (sort_order > 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_section'::regclass AND conname = 'ck_paper_section_score') THEN
        ALTER TABLE paper_section ADD CONSTRAINT ck_paper_section_score CHECK (score_per_question > 0 AND score_per_question <= 9999.99);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_section'::regclass AND conname = 'ck_paper_section_question_type') THEN
        ALTER TABLE paper_section ADD CONSTRAINT ck_paper_section_question_type
            CHECK (question_type IS NULL OR question_type IN ('SingleChoice','MultipleChoice','TrueFalse'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_section'::regclass AND conname = 'ck_paper_section_difficulty') THEN
        ALTER TABLE paper_section ADD CONSTRAINT ck_paper_section_difficulty
            CHECK (difficulty IS NULL OR difficulty IN ('Easy','Medium','Hard'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_section'::regclass AND conname = 'ck_paper_section_rule_mode') THEN
        ALTER TABLE paper_section ADD CONSTRAINT ck_paper_section_rule_mode CHECK (
            selection_mode = 'Random'
            OR (bank_id IS NULL AND category_id IS NULL AND question_type IS NULL AND difficulty IS NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_section'::regclass AND conname = 'ck_paper_section_bank_required') THEN
        ALTER TABLE paper_section ADD CONSTRAINT ck_paper_section_bank_required
            CHECK (selection_mode <> 'Random' OR bank_id IS NOT NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_section'::regclass AND conname = 'fk_paper_section_category_bank') THEN
        ALTER TABLE paper_section ADD CONSTRAINT fk_paper_section_category_bank
            FOREIGN KEY (bank_id, category_id) REFERENCES question_category(bank_id, id) ON DELETE RESTRICT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_question'::regclass AND conname = 'ck_paper_question_sort') THEN
        ALTER TABLE paper_question ADD CONSTRAINT ck_paper_question_sort CHECK (sort_order > 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'paper_question'::regclass AND conname = 'ck_paper_question_score') THEN
        ALTER TABLE paper_question ADD CONSTRAINT ck_paper_question_score CHECK (score > 0 AND score <= 9999.99);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_task'::regclass AND conname = 'ck_exam_task_retake_cooldown') THEN
        ALTER TABLE exam_task ADD CONSTRAINT ck_exam_task_retake_cooldown CHECK (retake_cooldown_minutes >= 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_assignment'::regclass AND conname = 'ck_exam_assignment_quota') THEN
        ALTER TABLE exam_assignment ADD CONSTRAINT ck_exam_assignment_quota CHECK (
            attempts_used >= 0 AND inherited_attempts_used >= 0
            AND (attempt_limit_override IS NULL OR attempt_limit_override > 0)
            AND (duration_minutes_override IS NULL OR (duration_minutes_override > 0 AND duration_minutes_override <= 1440)));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_attempt'::regclass AND conname = 'ck_exam_attempt_time_order') THEN
        ALTER TABLE exam_attempt ADD CONSTRAINT ck_exam_attempt_time_order CHECK (
            (submitted_at IS NULL OR submitted_at >= started_at)
            AND (graded_at IS NULL OR (submitted_at IS NOT NULL AND graded_at >= submitted_at))
            AND (status <> 'AutoSubmitted' OR submitted_at >= deadline_at + interval '30 seconds'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_attempt'::regclass AND conname = 'ck_exam_attempt_submission') THEN
        ALTER TABLE exam_attempt ADD CONSTRAINT ck_exam_attempt_submission CHECK (
            status NOT IN ('Submitted','AutoSubmitted','Graded') OR (submitted_at IS NOT NULL AND submit_key IS NOT NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_attempt'::regclass AND conname = 'ck_exam_attempt_in_progress') THEN
        ALTER TABLE exam_attempt ADD CONSTRAINT ck_exam_attempt_in_progress CHECK (
            status <> 'InProgress' OR (submitted_at IS NULL AND submit_key IS NULL AND auto_score IS NULL AND score IS NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_attempt'::regclass AND conname = 'ck_exam_attempt_graded') THEN
        ALTER TABLE exam_attempt ADD CONSTRAINT ck_exam_attempt_graded CHECK (
            status <> 'Graded' OR (graded_at IS NOT NULL AND auto_score IS NOT NULL AND score IS NOT NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_attempt'::regclass AND conname = 'ck_exam_attempt_auto_submitted') THEN
        ALTER TABLE exam_attempt ADD CONSTRAINT ck_exam_attempt_auto_submitted CHECK (status <> 'AutoSubmitted' OR is_late);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'exam_attempt'::regclass AND conname = 'ck_exam_attempt_voided') THEN
        ALTER TABLE exam_attempt ADD CONSTRAINT ck_exam_attempt_voided CHECK (
            status <> 'Voided' OR (voided_at IS NOT NULL AND char_length(void_reason) >= 10));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'retake_grant'::regclass AND conname = 'ck_retake_grant_reason') THEN
        ALTER TABLE retake_grant ADD CONSTRAINT ck_retake_grant_reason CHECK (char_length(reason) > 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'revocation_record'::regclass AND conname = 'ck_revocation_actor') THEN
        ALTER TABLE revocation_record ADD CONSTRAINT ck_revocation_actor CHECK (actor_id IS NOT NULL OR actor_role = 'System');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'revocation_record'::regclass AND conname = 'ck_revocation_actor_role') THEN
        ALTER TABLE revocation_record ADD CONSTRAINT ck_revocation_actor_role CHECK (actor_role IN ('User','System'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'outbox_message'::regclass AND conname = 'ck_outbox_retry') THEN
        ALTER TABLE outbox_message ADD CONSTRAINT ck_outbox_retry CHECK (retry_count >= 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'outbox_message'::regclass AND conname = 'ck_outbox_sent') THEN
        ALTER TABLE outbox_message ADD CONSTRAINT ck_outbox_sent CHECK (status <> 'Sent' OR sent_at IS NOT NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'outbox_message'::regclass AND conname = 'ck_outbox_payload') THEN
        ALTER TABLE outbox_message ADD CONSTRAINT ck_outbox_payload CHECK (jsonb_typeof(payload_json) = 'object');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'audit_event'::regclass AND conname = 'ck_audit_actor') THEN
        ALTER TABLE audit_event ADD CONSTRAINT ck_audit_actor CHECK (actor_id IS NOT NULL OR actor_role = 'System');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'audit_event'::regclass AND conname = 'ck_audit_idempotency_hash') THEN
        ALTER TABLE audit_event ADD CONSTRAINT ck_audit_idempotency_hash CHECK (idempotency_key IS NULL OR request_hash IS NOT NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'audit_event'::regclass AND conname = 'ck_audit_result_ref') THEN
        ALTER TABLE audit_event ADD CONSTRAINT ck_audit_result_ref CHECK (jsonb_typeof(result_ref_json) = 'object');
    END IF;
END $constraints$;

-- schema_meta compatibility: the former key/value table is collapsed without losing its declared version.
DO $meta$
DECLARE
    v_version text := '1.1.0';
    v_legacy boolean;
    v_initialized boolean;
BEGIN
    SELECT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'schema_meta' AND column_name = 'key'
    ) INTO v_legacy;
    SELECT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'schema_meta' AND column_name = 'id'
    ) INTO v_initialized;

    IF v_legacy AND NOT v_initialized THEN
        SELECT value INTO v_version FROM schema_meta WHERE key = 'schema_version';
        ALTER TABLE schema_meta ADD COLUMN IF NOT EXISTS id bigint NOT NULL DEFAULT 1;
        ALTER TABLE schema_meta ADD COLUMN IF NOT EXISTS schema_version text NULL;
        ALTER TABLE schema_meta ADD COLUMN IF NOT EXISTS app_compat_min text NULL;
        ALTER TABLE schema_meta ADD COLUMN IF NOT EXISTS app_compat_max text NULL;
        ALTER TABLE schema_meta ADD COLUMN IF NOT EXISTS checksum text NULL;
        ALTER TABLE schema_meta ADD COLUMN IF NOT EXISTS released_at date NULL;
        ALTER TABLE schema_meta ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now();
        DELETE FROM schema_meta;
        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'schema_meta'::regclass AND conname = 'ux_schema_meta_singleton') THEN
            ALTER TABLE schema_meta ADD CONSTRAINT ux_schema_meta_singleton UNIQUE (id);
        END IF;
        INSERT INTO schema_meta(id, key, value, schema_version, app_compat_min, app_compat_max, checksum, released_at)
        VALUES (1, 'singleton', COALESCE(v_version, '1.0.0'), COALESCE(v_version, '1.0.0'),
                COALESCE(v_version, '1.0.0'), COALESCE(v_version, '1.0.0'), NULL, DATE '2026-11-24');
        ALTER TABLE schema_meta ALTER COLUMN schema_version SET NOT NULL;
        ALTER TABLE schema_meta ALTER COLUMN app_compat_min SET NOT NULL;
        ALTER TABLE schema_meta ALTER COLUMN app_compat_max SET NOT NULL;
        ALTER TABLE schema_meta ALTER COLUMN released_at SET NOT NULL;
    ELSIF v_legacy THEN
        UPDATE schema_meta SET schema_version = '1.1.0', app_compat_min = '1.0.0',
            app_compat_max = '1.1.0', checksum = NULL, released_at = DATE '2026-11-26'
        WHERE id = 1;
    ELSE
        INSERT INTO schema_meta(id, schema_version, app_compat_min, app_compat_max, checksum, released_at)
        VALUES (1, '1.1.0', '1.0.0', '1.1.0', NULL, DATE '2026-11-26')
        ON CONFLICT (id) DO UPDATE SET schema_version = EXCLUDED.schema_version,
            app_compat_min = EXCLUDED.app_compat_min, app_compat_max = EXCLUDED.app_compat_max,
            checksum = EXCLUDED.checksum, released_at = EXCLUDED.released_at;
    END IF;
END $meta$;

DO $meta_constraints$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'schema_meta'::regclass AND conname = 'ck_schema_meta_singleton') THEN
        ALTER TABLE schema_meta ADD CONSTRAINT ck_schema_meta_singleton CHECK (id = 1);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'schema_meta'::regclass AND conname = 'ux_schema_meta_version') THEN
        ALTER TABLE schema_meta ADD CONSTRAINT ux_schema_meta_version UNIQUE (schema_version);
    END IF;
END $meta_constraints$;

-- Department materialized-path validation and cycle prevention.
CREATE OR REPLACE FUNCTION fn_check_department_path() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_parent_path text;
    v_parent bigint := NEW.parent_id;
    v_depth integer := 0;
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.path_segment IS DISTINCT FROM OLD.path_segment THEN
        RAISE EXCEPTION 'department path_segment is stable and cannot change' USING ERRCODE = '23514';
    END IF;
    IF NEW.path_segment !~ '^[0-9]{3}$' OR NEW.path !~ '^[0-9]{3}(\.[0-9]{3})*\.$' THEN
        RAISE EXCEPTION 'department path and path_segment must use three-digit segments' USING ERRCODE = '23514';
    END IF;
    IF right(NEW.path, 4) <> (NEW.path_segment || '.') THEN
        RAISE EXCEPTION 'department path_segment must match the final path segment' USING ERRCODE = '23514';
    END IF;
    IF NEW.parent_id IS NULL THEN
        IF NEW.path <> NEW.path_segment || '.' THEN
            RAISE EXCEPTION 'root department path must contain only its own segment' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.parent_id = NEW.id THEN
        RAISE EXCEPTION 'department parent cannot be itself' USING ERRCODE = '23514';
    END IF;
    SELECT path INTO v_parent_path FROM department WHERE id = NEW.parent_id;
    IF v_parent_path IS NULL OR NEW.path <> v_parent_path || NEW.path_segment || '.' THEN
        RAISE EXCEPTION 'department path must extend its parent path' USING ERRCODE = '23514';
    END IF;
    WHILE v_parent IS NOT NULL LOOP
        v_depth := v_depth + 1;
        IF v_depth > 64 OR v_parent = NEW.id THEN
            RAISE EXCEPTION 'department cycle detected' USING ERRCODE = '23514';
        END IF;
        SELECT parent_id INTO v_parent FROM department WHERE id = v_parent;
    END LOOP;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_department_cycle ON department;
DROP TRIGGER IF EXISTS trg_department_path_validate ON department;
CREATE TRIGGER trg_department_path_validate
BEFORE INSERT OR UPDATE OF parent_id, path, path_segment ON department
FOR EACH ROW EXECUTE FUNCTION fn_check_department_path();

-- Role scope anchors and system-role protection.
CREATE OR REPLACE FUNCTION fn_check_user_role_scope() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_scope text;
    v_active boolean;
BEGIN
    SELECT scope_type, is_active INTO v_scope, v_active FROM role WHERE id = NEW.role_id;
    IF v_scope IS NULL OR NOT v_active THEN
        RAISE EXCEPTION 'user_role requires an existing active role' USING ERRCODE = '23514';
    END IF;
    IF v_scope IN ('Department','DepartmentSubtree') THEN
        IF NEW.department_id IS NULL THEN
            RAISE EXCEPTION 'department-scoped role authorization requires department_id' USING ERRCODE = '23514';
        END IF;
    ELSIF NEW.department_id IS NOT NULL THEN
        RAISE EXCEPTION 'All, Assigned and Self role authorizations must have NULL department_id' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_user_role_scope ON user_role;
DROP TRIGGER IF EXISTS trg_user_role_scope_validate ON user_role;
CREATE TRIGGER trg_user_role_scope_validate BEFORE INSERT OR UPDATE ON user_role
FOR EACH ROW EXECUTE FUNCTION fn_check_user_role_scope();

-- Initialize scope semantics for roles created by the previous schema before protecting system roles.
UPDATE role SET scope_type = CASE code
    WHEN 'PlatformAdmin' THEN 'All'
    WHEN 'OrgAdmin' THEN 'All'
    WHEN 'Auditor' THEN 'All'
    WHEN 'ReadOnlyAuditor' THEN 'All'
    WHEN 'Candidate' THEN 'Self'
    WHEN 'Grader' THEN 'Assigned'
    ELSE 'DepartmentSubtree'
END
WHERE code IN ('PlatformAdmin','OrgAdmin','DeptAdmin','DepartmentAdmin','ExamManager','QuestionManager',
               'Grader','Invigilator','Proctor','Auditor','ReadOnlyAuditor','Candidate');

CREATE OR REPLACE FUNCTION fn_guard_system_role() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.is_system THEN
        IF TG_OP = 'DELETE' THEN
            IF OLD.code IN ('DepartmentAdmin','Proctor','ReadOnlyAuditor') THEN RETURN OLD; END IF;
            RAISE EXCEPTION 'system roles cannot be deleted' USING ERRCODE = '23514';
        END IF;
        IF NEW.code IS DISTINCT FROM OLD.code THEN
            IF (OLD.code = 'DepartmentAdmin' AND NEW.code = 'DeptAdmin')
               OR (OLD.code = 'Proctor' AND NEW.code = 'Invigilator') THEN RETURN NEW; END IF;
            RAISE EXCEPTION 'system role code cannot change' USING ERRCODE = '23514';
        END IF;
        IF NEW.is_system IS DISTINCT FROM OLD.is_system OR NEW.scope_type IS DISTINCT FROM OLD.scope_type THEN
            RAISE EXCEPTION 'system role type and scope cannot change' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_role_system_guard ON role;
CREATE TRIGGER trg_role_system_guard BEFORE UPDATE OR DELETE ON role
FOR EACH ROW EXECUTE FUNCTION fn_guard_system_role();

-- Published question versions and their options are immutable; publication validates answer/option agreement.
CREATE OR REPLACE FUNCTION fn_assert_question_answer(p_version_id bigint, p_answer jsonb, p_type text, p_require_options boolean)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_keys text[];
    v_labels text[];
    v_count integer;
    v_correct integer;
    v_value jsonb;
BEGIN
    IF p_answer IS NULL OR p_answer = '{}'::jsonb THEN
        IF p_require_options THEN RAISE EXCEPTION 'published question answer cannot be empty' USING ERRCODE = '23514'; END IF;
        RETURN;
    END IF;
    IF jsonb_typeof(p_answer) <> 'object' THEN
        RAISE EXCEPTION 'answer_json must be an object' USING ERRCODE = '23514';
    END IF;
    SELECT array_agg(k.key ORDER BY k.key) INTO v_keys FROM jsonb_object_keys(p_answer) AS k(key);
    IF p_type = 'SingleChoice' THEN
        IF v_keys IS DISTINCT FROM ARRAY['value'] OR jsonb_typeof(p_answer -> 'value') <> 'string' THEN
            RAISE EXCEPTION 'SingleChoice answer must contain only string value' USING ERRCODE = '23514';
        END IF;
    ELSIF p_type = 'MultipleChoice' THEN
        IF v_keys IS DISTINCT FROM ARRAY['values'] OR jsonb_typeof(p_answer -> 'values') <> 'array' THEN
            RAISE EXCEPTION 'MultipleChoice answer must contain only values array' USING ERRCODE = '23514';
        END IF;
        IF EXISTS (SELECT 1 FROM jsonb_array_elements_text(p_answer -> 'values') v GROUP BY v HAVING count(*) > 1) THEN
            RAISE EXCEPTION 'MultipleChoice answer values must be unique' USING ERRCODE = '23514';
        END IF;
    ELSIF p_type = 'TrueFalse' THEN
        IF v_keys IS DISTINCT FROM ARRAY['value'] OR jsonb_typeof(p_answer -> 'value') <> 'boolean' THEN
            RAISE EXCEPTION 'TrueFalse answer must contain only boolean value' USING ERRCODE = '23514';
        END IF;
    ELSE
        RAISE EXCEPTION 'unsupported question type' USING ERRCODE = '23514';
    END IF;

    SELECT count(*), count(*) FILTER (WHERE is_correct),
           array_agg(label ORDER BY label) FILTER (WHERE is_correct)
    INTO v_count, v_correct, v_labels FROM question_option WHERE question_version_id = p_version_id;
    IF NOT p_require_options AND v_count = 0 THEN RETURN; END IF;
    IF p_type = 'SingleChoice' THEN
        IF v_count < 2 OR v_correct IS DISTINCT FROM 1
           OR NOT EXISTS (SELECT 1 FROM question_option WHERE question_version_id = p_version_id AND label = p_answer ->> 'value' AND is_correct) THEN
            RAISE EXCEPTION 'SingleChoice options and answer are inconsistent' USING ERRCODE = '23514';
        END IF;
    ELSIF p_type = 'MultipleChoice' THEN
        IF v_count < 2 OR v_correct < 2 OR v_labels IS DISTINCT FROM (
            SELECT array_agg(value ORDER BY value) FROM jsonb_array_elements_text(p_answer -> 'values')
        ) THEN
            RAISE EXCEPTION 'MultipleChoice options and answer set are inconsistent' USING ERRCODE = '23514';
        END IF;
    ELSIF p_type = 'TrueFalse' THEN
        IF v_count IS DISTINCT FROM 2 OR v_labels IS DISTINCT FROM ARRAY[
            CASE WHEN (p_answer ->> 'value')::boolean THEN 'A' ELSE 'B' END
        ] OR NOT EXISTS (
            SELECT 1 FROM question_option o WHERE o.question_version_id = p_version_id
            AND ((o.label = 'A' AND o.content = '正确') OR (o.label = 'B' AND o.content = '错误'))
        ) THEN
            RAISE EXCEPTION 'TrueFalse options must be A=正确, B=错误 and match the answer' USING ERRCODE = '23514';
        END IF;
    END IF;
END $$;

CREATE OR REPLACE FUNCTION fn_guard_question_version() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_type text;
    v_bank_id bigint;
    v_import_bank_id bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status = 'Published' THEN RAISE EXCEPTION 'published question versions cannot be deleted' USING ERRCODE = '23514'; END IF;
        RETURN OLD;
    END IF;
    IF TG_OP = 'INSERT' AND NEW.status <> 'Draft' THEN
        RAISE EXCEPTION 'question versions must be created as Draft' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status = 'Published' THEN
        RAISE EXCEPTION 'published question versions are immutable' USING ERRCODE = '23514';
    END IF;
    SELECT type INTO v_type FROM question WHERE id = NEW.question_id;
    IF NEW.source = 'Import' THEN
        SELECT q.bank_id INTO v_bank_id FROM question q WHERE q.id = NEW.question_id;
        SELECT bank_id INTO v_import_bank_id FROM import_batch WHERE id = NEW.import_batch_id;
        IF v_bank_id IS DISTINCT FROM v_import_bank_id THEN
            RAISE EXCEPTION 'imported question version must belong to the import batch bank' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF NEW.status = 'Published' THEN
        IF NEW.published_at IS NULL THEN RAISE EXCEPTION 'published_at is required' USING ERRCODE = '23514'; END IF;
        PERFORM fn_assert_question_answer(NEW.id, NEW.answer_json, v_type, true);
    ELSE
        PERFORM fn_assert_question_answer(NEW.id, NEW.answer_json, v_type, false);
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_question_version_guard ON question_version;
CREATE TRIGGER trg_question_version_guard BEFORE INSERT OR UPDATE OR DELETE ON question_version
FOR EACH ROW EXECUTE FUNCTION fn_guard_question_version();

CREATE OR REPLACE FUNCTION fn_guard_question_option() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_status text;
    v_version_id bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        v_version_id := OLD.question_version_id;
    ELSE
        v_version_id := NEW.question_version_id;
    END IF;
    SELECT status INTO v_status FROM question_version WHERE id = v_version_id;
    IF v_status = 'Published' THEN
        RAISE EXCEPTION 'published question options are immutable' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_question_option_guard ON question_option;
CREATE TRIGGER trg_question_option_guard BEFORE INSERT OR UPDATE OR DELETE ON question_option
FOR EACH ROW EXECUTE FUNCTION fn_guard_question_option();

CREATE OR REPLACE FUNCTION fn_guard_question_tag_link() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_status text;
    v_version_id bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN v_version_id := OLD.question_version_id; ELSE v_version_id := NEW.question_version_id; END IF;
    SELECT status INTO v_status FROM question_version WHERE id = v_version_id;
    IF v_status = 'Published' THEN
        RAISE EXCEPTION 'published question tag links are immutable' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_question_tag_link_guard ON question_tag_link;
CREATE TRIGGER trg_question_tag_link_guard BEFORE INSERT OR UPDATE OR DELETE ON question_tag_link
FOR EACH ROW EXECUTE FUNCTION fn_guard_question_tag_link();

CREATE OR REPLACE FUNCTION fn_guard_question_category_link() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_version_status text;
    v_version_bank bigint;
    v_category_bank bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        SELECT qv.status, q.bank_id INTO v_version_status, v_version_bank
        FROM question_version qv JOIN question q ON q.id = qv.question_id
        WHERE qv.id = OLD.question_version_id;
    ELSE
        SELECT qv.status, q.bank_id INTO v_version_status, v_version_bank
        FROM question_version qv JOIN question q ON q.id = qv.question_id
        WHERE qv.id = NEW.question_version_id;
        SELECT bank_id INTO v_category_bank FROM question_category WHERE id = NEW.category_id;
        IF v_category_bank IS DISTINCT FROM v_version_bank THEN
            RAISE EXCEPTION 'question category must belong to the question bank' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF v_version_status = 'Published' THEN
        RAISE EXCEPTION 'published question category links are immutable' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_question_category_link_guard ON question_category_link;
CREATE TRIGGER trg_question_category_link_guard BEFORE INSERT OR UPDATE OR DELETE ON question_category_link
FOR EACH ROW EXECUTE FUNCTION fn_guard_question_category_link();

CREATE OR REPLACE FUNCTION fn_check_question_pointer() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_owner bigint;
    v_status text;
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'questions cannot be physically deleted' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'INSERT' AND NEW.status <> 'Draft' THEN
        RAISE EXCEPTION 'questions must be created as Draft' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.status = 'Archived' THEN RAISE EXCEPTION 'archived questions are immutable' USING ERRCODE = '23514'; END IF;
        IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
            (OLD.status = 'Draft' AND NEW.status IN ('Published','Archived'))
            OR (OLD.status = 'Published' AND NEW.status = 'Archived')
        ) THEN
            RAISE EXCEPTION 'invalid question state transition' USING ERRCODE = '23514';
        END IF;
        IF OLD.status <> 'Draft' AND (NEW.type IS DISTINCT FROM OLD.type OR NEW.bank_id IS DISTINCT FROM OLD.bank_id) THEN
            RAISE EXCEPTION 'published question identity cannot change' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF NEW.current_version_id IS NOT NULL THEN
        SELECT question_id, status INTO v_owner, v_status FROM question_version WHERE id = NEW.current_version_id;
        IF v_owner IS DISTINCT FROM NEW.id THEN RAISE EXCEPTION 'current_version_id must belong to the question' USING ERRCODE = '23514'; END IF;
    END IF;
    IF NEW.status = 'Published' AND (NEW.current_version_id IS NULL OR v_status IS DISTINCT FROM 'Published') THEN
        RAISE EXCEPTION 'published question requires its published current version' USING ERRCODE = '23514';
    END IF;
    IF NEW.status = 'Archived' AND NEW.archived_at IS NULL THEN
        RAISE EXCEPTION 'archived_at is required when archiving a question' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_question_pointer ON question;
DROP TRIGGER IF EXISTS trg_question_pointer_validate ON question;
CREATE TRIGGER trg_question_pointer_validate BEFORE INSERT OR UPDATE OR DELETE ON question
FOR EACH ROW EXECUTE FUNCTION fn_check_question_pointer();

-- Import lifecycle is append-forward; terminal batches and row results cannot be rewritten.
CREATE OR REPLACE FUNCTION fn_guard_import_batch() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' AND NEW.status NOT IN ('Uploaded','Validating','Previewed') THEN
        RAISE EXCEPTION 'import batches must start in Uploaded, Validating or Previewed' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.status IN ('Completed','PartiallyFailed','Failed') THEN
            RAISE EXCEPTION 'terminal import batches are immutable' USING ERRCODE = '23514';
        END IF;
        IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
        (OLD.status = 'Uploaded' AND NEW.status IN ('Validating','Previewed','Failed'))
        OR (OLD.status = 'Validating' AND NEW.status IN ('Previewed','Failed'))
        OR (OLD.status = 'Previewed' AND NEW.status IN ('Importing','Failed'))
        OR (OLD.status = 'Importing' AND NEW.status IN ('Completed','PartiallyFailed','Failed'))
    ) THEN
        RAISE EXCEPTION 'invalid import batch state transition' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_import_batch_guard ON import_batch;
CREATE TRIGGER trg_import_batch_guard BEFORE INSERT OR UPDATE ON import_batch
FOR EACH ROW EXECUTE FUNCTION fn_guard_import_batch();

CREATE OR REPLACE FUNCTION fn_guard_import_row() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' AND NEW.status NOT IN ('Valid','Invalid','Warning') THEN
        RAISE EXCEPTION 'import rows must be created with a preview validation status' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'import rows cannot be physically deleted while their batch is retained' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.status IN ('Imported','Excluded') THEN
            RAISE EXCEPTION 'terminal import rows are immutable' USING ERRCODE = '23514';
        END IF;
        IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
            (OLD.status IN ('Valid','Warning') AND NEW.status IN ('Imported','Excluded'))
            OR (OLD.status = 'Invalid' AND NEW.status = 'Excluded')
        ) THEN
            RAISE EXCEPTION 'invalid import row state transition' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_import_row_guard ON import_row;
CREATE TRIGGER trg_import_row_guard BEFORE INSERT OR UPDATE OR DELETE ON import_row
FOR EACH ROW EXECUTE FUNCTION fn_guard_import_row();

-- Paper publication checks structure, scoring and random pools; published/archived snapshots and children are immutable.
CREATE OR REPLACE FUNCTION fn_assert_paper_ready(p_version_id bigint) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_section record;
    v_expected numeric(12,2) := 0;
    v_actual numeric(12,2);
    v_count integer;
    v_bad_count integer;
BEGIN
    SELECT count(*) INTO v_count FROM paper_section WHERE paper_version_id = p_version_id;
    IF v_count = 0 THEN RAISE EXCEPTION 'published paper requires sections' USING ERRCODE = '23514'; END IF;
    FOR v_section IN SELECT * FROM paper_section WHERE paper_version_id = p_version_id ORDER BY sort_order, id LOOP
        SELECT count(*) INTO v_count FROM paper_question WHERE section_id = v_section.id;
        SELECT count(*) INTO v_bad_count FROM paper_question pq JOIN question_version qv ON qv.id = pq.question_version_id
        WHERE pq.section_id = v_section.id AND (qv.status <> 'Published' OR pq.score <> v_section.score_per_question
            OR pq.is_pool <> (v_section.selection_mode = 'Random'));
        IF v_bad_count > 0 THEN RAISE EXCEPTION 'paper questions must match section mode, score and published question version' USING ERRCODE = '23514'; END IF;
        IF v_section.selection_mode = 'Fixed' THEN
            IF v_count = 0 THEN RAISE EXCEPTION 'Fixed section requires questions' USING ERRCODE = '23514'; END IF;
            SELECT COALESCE(sum(score), 0) INTO v_actual FROM paper_question WHERE section_id = v_section.id;
            v_expected := v_expected + v_actual;
        ELSE
            IF v_count < v_section.pick_count THEN RAISE EXCEPTION 'Random section pool is smaller than pick_count' USING ERRCODE = '23514'; END IF;
            v_expected := v_expected + v_section.pick_count * v_section.score_per_question;
        END IF;
    END LOOP;
    SELECT total_score INTO v_actual FROM paper_version WHERE id = p_version_id;
    IF v_actual IS DISTINCT FROM v_expected THEN
        RAISE EXCEPTION 'paper total_score does not match fixed questions plus random selections' USING ERRCODE = '23514';
    END IF;
END $$;

CREATE OR REPLACE FUNCTION fn_guard_paper_version() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status IN ('Published','Archived') THEN RAISE EXCEPTION 'published paper versions cannot be deleted' USING ERRCODE = '23514'; END IF;
        RETURN OLD;
    END IF;
    IF TG_OP = 'INSERT' AND NEW.status <> 'Draft' THEN
        RAISE EXCEPTION 'paper versions must be created as Draft' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status IN ('Published','Archived') THEN
        IF OLD.status = 'Published' AND NEW.status = 'Archived'
           AND ROW(NEW.paper_id, NEW.version_no, NEW.title, NEW.total_score, NEW.duration_minutes,
                   NEW.published_at, NEW.published_by) IS NOT DISTINCT FROM
               ROW(OLD.paper_id, OLD.version_no, OLD.title, OLD.total_score, OLD.duration_minutes,
                   OLD.published_at, OLD.published_by) THEN
            RETURN NEW;
        END IF;
        RAISE EXCEPTION 'published paper versions are immutable except archival status' USING ERRCODE = '23514';
    END IF;
    IF NEW.status = 'Published' THEN
        IF NEW.published_at IS NULL THEN RAISE EXCEPTION 'published_at is required' USING ERRCODE = '23514'; END IF;
        PERFORM fn_assert_paper_ready(NEW.id);
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_paper_version_guard ON paper_version;
CREATE TRIGGER trg_paper_version_guard BEFORE INSERT OR UPDATE OR DELETE ON paper_version
FOR EACH ROW EXECUTE FUNCTION fn_guard_paper_version();

CREATE OR REPLACE FUNCTION fn_guard_paper_child() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_status text;
    v_mode text;
    v_question_status text;
    v_version_id bigint;
    v_section_id bigint;
    v_question_version_id bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN
        v_version_id := OLD.paper_version_id;
        IF TG_TABLE_NAME = 'paper_section' THEN
            v_section_id := OLD.id;
        ELSE
            v_section_id := OLD.section_id;
            v_question_version_id := OLD.question_version_id;
        END IF;
    ELSE
        v_version_id := NEW.paper_version_id;
        IF TG_TABLE_NAME = 'paper_section' THEN
            v_section_id := NEW.id;
        ELSE
            v_section_id := NEW.section_id;
            v_question_version_id := NEW.question_version_id;
        END IF;
    END IF;
    IF TG_TABLE_NAME = 'paper_section' THEN
        SELECT status INTO v_status FROM paper_version WHERE id = v_version_id;
        IF TG_OP = 'UPDATE' AND NEW.selection_mode IS DISTINCT FROM OLD.selection_mode AND EXISTS (
            SELECT 1 FROM paper_question WHERE section_id = NEW.id
            AND is_pool <> (NEW.selection_mode = 'Random')
        ) THEN
            RAISE EXCEPTION 'section selection_mode must match existing paper_question.is_pool' USING ERRCODE = '23514';
        END IF;
    ELSE
        SELECT status INTO v_status FROM paper_version WHERE id = v_version_id;
        SELECT selection_mode INTO v_mode FROM paper_section WHERE id = v_section_id;
        SELECT status INTO v_question_status FROM question_version WHERE id = v_question_version_id;
        IF TG_OP = 'INSERT' AND v_question_status <> 'Published' THEN
            RAISE EXCEPTION 'paper questions require published question versions' USING ERRCODE = '23514';
        END IF;
        IF TG_OP IN ('INSERT','UPDATE') AND NEW.is_pool <> (v_mode = 'Random') THEN
            RAISE EXCEPTION 'paper_question.is_pool must match section selection_mode' USING ERRCODE = '23514';
        END IF;
        IF TG_OP = 'UPDATE' AND NEW.question_version_id IS DISTINCT FROM OLD.question_version_id AND v_question_status <> 'Published' THEN
            RAISE EXCEPTION 'paper questions require published question versions' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF v_status IN ('Published','Archived') THEN
        RAISE EXCEPTION 'published paper child rows are immutable' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_paper_section_guard ON paper_section;
CREATE TRIGGER trg_paper_section_guard BEFORE INSERT OR UPDATE OR DELETE ON paper_section
FOR EACH ROW EXECUTE FUNCTION fn_guard_paper_child();
DROP TRIGGER IF EXISTS trg_paper_question_guard ON paper_question;
CREATE TRIGGER trg_paper_question_guard BEFORE INSERT OR UPDATE OR DELETE ON paper_question
FOR EACH ROW EXECUTE FUNCTION fn_guard_paper_child();

CREATE OR REPLACE FUNCTION fn_check_paper_pointer() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_owner bigint;
    v_status text;
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'papers cannot be physically deleted' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'INSERT' AND NEW.status <> 'Draft' THEN
        RAISE EXCEPTION 'papers must be created as Draft' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.status = 'Archived' THEN RAISE EXCEPTION 'archived papers are immutable' USING ERRCODE = '23514'; END IF;
        IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
            (OLD.status = 'Draft' AND NEW.status IN ('Published','Archived'))
            OR (OLD.status = 'Published' AND NEW.status = 'Archived')
        ) THEN
            RAISE EXCEPTION 'invalid paper state transition' USING ERRCODE = '23514';
        END IF;
        IF OLD.status <> 'Draft' AND NEW.department_id IS DISTINCT FROM OLD.department_id THEN
            RAISE EXCEPTION 'published paper department cannot change' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF NEW.current_version_id IS NOT NULL THEN
        SELECT paper_id, status INTO v_owner, v_status FROM paper_version WHERE id = NEW.current_version_id;
        IF v_owner IS DISTINCT FROM NEW.id THEN RAISE EXCEPTION 'current_version_id must belong to the paper' USING ERRCODE = '23514'; END IF;
    END IF;
    IF NEW.status = 'Published' AND (NEW.current_version_id IS NULL OR v_status IS DISTINCT FROM 'Published') THEN
        RAISE EXCEPTION 'published paper requires its published current version' USING ERRCODE = '23514';
    END IF;
    IF NEW.status = 'Archived' AND NEW.archived_at IS NULL THEN
        RAISE EXCEPTION 'archived_at is required when archiving a paper' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_paper_pointer ON paper;
DROP TRIGGER IF EXISTS trg_paper_pointer_validate ON paper;
CREATE TRIGGER trg_paper_pointer_validate BEFORE INSERT OR UPDATE OR DELETE ON paper
FOR EACH ROW EXECUTE FUNCTION fn_check_paper_pointer();

-- Batch progress is durable and append-forward; retry only reopens a failed row.
CREATE OR REPLACE FUNCTION fn_guard_assignment_batch() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' AND NEW.status <> 'Pending' THEN
        RAISE EXCEPTION 'assignment batches must be created as Pending' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.status = 'Revoked' THEN
            RAISE EXCEPTION 'revoked assignment batches are immutable' USING ERRCODE = '23514';
        END IF;
        IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
        (OLD.status = 'Pending' AND NEW.status IN ('Running','Revoking','Completed','PartiallyFailed','Failed','Revoked'))
        OR (OLD.status = 'Running' AND NEW.status IN ('Revoking','Completed','PartiallyFailed','Failed','Revoked'))
        OR (OLD.status IN ('Failed','PartiallyFailed') AND NEW.status = 'Running')
        OR (OLD.status = 'Revoking' AND NEW.status = 'Revoked')
    ) THEN
        RAISE EXCEPTION 'invalid assignment batch state transition' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_assignment_batch_guard ON assignment_batch;
CREATE TRIGGER trg_assignment_batch_guard BEFORE INSERT OR UPDATE ON assignment_batch
FOR EACH ROW EXECUTE FUNCTION fn_guard_assignment_batch();

CREATE OR REPLACE FUNCTION fn_guard_assignment_batch_row() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'assignment batch rows cannot be physically deleted' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'UPDATE' AND EXISTS (
        SELECT 1 FROM assignment_batch WHERE id = OLD.batch_id
          AND status IN ('Completed','Revoked')
    ) THEN
        RAISE EXCEPTION 'terminal assignment batch rows are immutable' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.status IS DISTINCT FROM OLD.status AND NOT (
        (OLD.status = 'Pending' AND NEW.status IN ('Processing','Skipped','Failed','Revoked'))
        OR (OLD.status = 'Processing' AND NEW.status IN ('Success','Skipped','Failed','Revoked'))
        OR (OLD.status = 'Failed' AND NEW.status IN ('Processing','Skipped','Revoked'))
        OR (OLD.status = 'Success' AND NEW.status = 'Revoked')
    ) THEN
        RAISE EXCEPTION 'invalid assignment batch row state transition' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_assignment_batch_row_guard ON assignment_batch_row;
CREATE TRIGGER trg_assignment_batch_row_guard BEFORE INSERT OR UPDATE OR DELETE ON assignment_batch_row
FOR EACH ROW EXECUTE FUNCTION fn_guard_assignment_batch_row();

-- Task publication locks its department, schedule and rules. Legacy retake_deadline remains unused.
CREATE OR REPLACE FUNCTION fn_guard_exam_task() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_paper_department bigint;
    v_version_status text;
    v_has_assignment boolean;
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status <> 'Draft' OR EXISTS (SELECT 1 FROM exam_assignment WHERE task_id = OLD.id) THEN
            RAISE EXCEPTION 'only a Draft task without assignments can be deleted' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;
    IF TG_OP = 'INSERT' AND NEW.status <> 'Draft' THEN
        RAISE EXCEPTION 'exam tasks must be created as Draft' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status = 'Archived' THEN
        RAISE EXCEPTION 'archived exam tasks are immutable' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.status IS DISTINCT FROM OLD.status AND NOT (
        (OLD.status = 'Draft' AND NEW.status IN ('Published','Archived'))
        OR (OLD.status = 'Published' AND NEW.status IN ('Closed','Revoked'))
        OR (OLD.status = 'Closed' AND NEW.status IN ('Published','Archived'))
        OR (OLD.status = 'Revoked' AND NEW.status = 'Archived')
    ) THEN
        RAISE EXCEPTION 'invalid exam task state transition' USING ERRCODE = '23514';
    END IF;
    IF NEW.paper_version_id IS NOT NULL THEN
        SELECT p.department_id, pv.status INTO v_paper_department, v_version_status
        FROM paper_version pv JOIN paper p ON p.id = pv.paper_id WHERE pv.id = NEW.paper_version_id;
        IF v_paper_department IS DISTINCT FROM NEW.department_id THEN
            RAISE EXCEPTION 'task department must match paper department' USING ERRCODE = '23514';
        END IF;
        IF TG_OP = 'INSERT' OR NEW.paper_version_id IS DISTINCT FROM OLD.paper_version_id
           OR (OLD.status = 'Draft' AND NEW.status = 'Published') THEN
            IF v_version_status <> 'Published' THEN RAISE EXCEPTION 'task requires a Published paper version' USING ERRCODE = '23514'; END IF;
        END IF;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.status <> 'Draft' THEN
        IF ROW(NEW.department_id, NEW.code, NEW.exam_date, NEW.paper_version_id, NEW.available_from, NEW.available_to,
               NEW.duration_minutes, NEW.attempt_limit, NEW.score_policy, NEW.retake_approval,
               NEW.retake_cooldown_minutes, NEW.retake_deadline_at, NEW.pass_score, NEW.result_release_policy,
               NEW.late_score_policy)
           IS DISTINCT FROM
           ROW(OLD.department_id, OLD.code, OLD.exam_date, OLD.paper_version_id, OLD.available_from, OLD.available_to,
               OLD.duration_minutes, OLD.attempt_limit, OLD.score_policy, OLD.retake_approval,
               OLD.retake_cooldown_minutes, OLD.retake_deadline_at, OLD.pass_score, OLD.result_release_policy,
               OLD.late_score_policy) THEN
            RAISE EXCEPTION 'published task schedule and rules are immutable' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF NEW.status IN ('Published','Closed','Revoked') AND NEW.published_at IS NULL THEN
        RAISE EXCEPTION 'published_at is required for a published task lifecycle' USING ERRCODE = '23514';
    END IF;
    IF NEW.status = 'Archived' AND NEW.archived_at IS NULL THEN
        RAISE EXCEPTION 'archived_at is required when archiving a task' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.result_status IS DISTINCT FROM OLD.result_status AND NOT (
        (OLD.result_status = 'Unpublished' AND NEW.result_status = 'Published')
        OR (OLD.result_status = 'Published' AND NEW.result_status = 'Withdrawn')
        OR (OLD.result_status = 'Withdrawn' AND NEW.result_status = 'Published')
    ) THEN
        RAISE EXCEPTION 'invalid result publication state transition' USING ERRCODE = '23514';
    END IF;
    IF NEW.result_status = 'Published' AND (TG_OP = 'INSERT' OR OLD.result_status <> 'Published') AND NEW.result_published_at IS NULL THEN
        RAISE EXCEPTION 'result_published_at is required when publishing results' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_exam_task_guard ON exam_task;
CREATE TRIGGER trg_exam_task_guard BEFORE INSERT OR UPDATE OR DELETE ON exam_task
FOR EACH ROW EXECUTE FUNCTION fn_guard_exam_task();

CREATE OR REPLACE FUNCTION fn_guard_exam_assignment() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_task_status text;
    v_from timestamp;
    v_to timestamp;
    v_user_status text;
    v_old record;
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'exam assignments cannot be physically deleted' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'UPDATE' AND OLD.status IN ('Closed','Expired','Revoked') THEN
        RAISE EXCEPTION 'terminal exam assignments are immutable' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'INSERT' AND NEW.status <> 'Pending' THEN
        RAISE EXCEPTION 'exam assignments must be created as Pending' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.status IS DISTINCT FROM OLD.status AND NOT (
        (OLD.status = 'Pending' AND NEW.status IN ('Available','Expired','Revoked'))
        OR (OLD.status = 'Available' AND NEW.status IN ('InProgress','Expired','Revoked'))
        OR (OLD.status = 'InProgress' AND NEW.status IN ('Submitted','Revoked'))
        OR (OLD.status = 'InProgress' AND NEW.status = 'Available' AND NOT EXISTS (SELECT 1 FROM exam_attempt WHERE assignment_id = OLD.id AND status = 'InProgress'))
        OR (OLD.status = 'InProgress' AND NEW.status IN ('Closed','Expired') AND NOT EXISTS (SELECT 1 FROM exam_attempt WHERE assignment_id = OLD.id AND status = 'InProgress'))
        OR (OLD.status = 'Submitted' AND NEW.status IN ('Graded','Available','Closed','Expired','Revoked'))
        OR (OLD.status = 'Graded' AND NEW.status IN ('Available','Closed','Expired','Revoked'))
    ) THEN
        RAISE EXCEPTION 'invalid exam assignment state transition' USING ERRCODE = '23514';
    END IF;
    SELECT status, available_from, available_to INTO v_task_status, v_from, v_to FROM exam_task WHERE id = NEW.task_id;
    SELECT status INTO v_user_status FROM app_user WHERE id = NEW.user_id;
    IF v_task_status IS NULL OR v_user_status IS NULL THEN RAISE EXCEPTION 'assignment task or user not found' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'INSERT' AND v_task_status <> 'Published' THEN RAISE EXCEPTION 'assignments require a Published task' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'INSERT' AND v_user_status <> 'Active' THEN RAISE EXCEPTION 'assignments require an Active user' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'INSERT' AND (NEW.available_from < v_from OR NEW.available_to > v_to) THEN
        RAISE EXCEPTION 'assignment window must be within the task window' USING ERRCODE = '23514';
    END IF;
    IF NEW.reissued_from_id IS NOT NULL THEN
        SELECT * INTO v_old FROM exam_assignment WHERE id = NEW.reissued_from_id;
        IF v_old.id IS NULL OR v_old.status <> 'Revoked' OR v_old.task_id <> NEW.task_id OR v_old.user_id <> NEW.user_id THEN
            RAISE EXCEPTION 'reissued assignment must reference a revoked assignment for the same task and user' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_exam_assignment_guard ON exam_assignment;
CREATE TRIGGER trg_exam_assignment_guard BEFORE INSERT OR UPDATE ON exam_assignment
FOR EACH ROW EXECUTE FUNCTION fn_guard_exam_assignment();

-- Attempt lifecycle guards. Answer content freezes at submit; only grading columns may change during auto-grading.
CREATE OR REPLACE FUNCTION fn_guard_exam_attempt() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_pending integer;
    v_answer_score numeric(12,2);
    v_answer_max numeric(12,2);
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'exam attempts cannot be physically deleted' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'INSERT' AND NEW.status <> 'InProgress' THEN
        RAISE EXCEPTION 'exam attempts must be created as InProgress' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.status = 'Voided' THEN
            RAISE EXCEPTION 'voided exam attempts are immutable' USING ERRCODE = '23514';
        END IF;
        IF ROW(NEW.assignment_id, NEW.attempt_no, NEW.start_key, NEW.started_at, NEW.deadline_at, NEW.max_score, NEW.paper_snapshot_json)
           IS DISTINCT FROM
           ROW(OLD.assignment_id, OLD.attempt_no, OLD.start_key, OLD.started_at, OLD.deadline_at, OLD.max_score, OLD.paper_snapshot_json) THEN
            RAISE EXCEPTION 'attempt identity, timing and paper snapshot are immutable' USING ERRCODE = '23514';
        END IF;
        IF OLD.auto_score IS NOT NULL AND NEW.auto_score IS DISTINCT FROM OLD.auto_score THEN
            RAISE EXCEPTION 'auto_score is an immutable grading fact' USING ERRCODE = '23514';
        END IF;
        IF OLD.submitted_at IS NOT NULL AND (NEW.submitted_at IS DISTINCT FROM OLD.submitted_at OR NEW.submit_key IS DISTINCT FROM OLD.submit_key OR NEW.is_late IS DISTINCT FROM OLD.is_late) THEN
            RAISE EXCEPTION 'submission facts are immutable' USING ERRCODE = '23514';
        END IF;
        IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
            (OLD.status = 'InProgress' AND NEW.status IN ('Submitted','AutoSubmitted','Voided'))
            OR (OLD.status IN ('Submitted','AutoSubmitted') AND NEW.status IN ('Graded','Voided'))
            OR (OLD.status = 'Graded' AND NEW.status = 'Voided')
        ) THEN
            RAISE EXCEPTION 'invalid exam attempt state transition' USING ERRCODE = '23514';
        END IF;
        IF NEW.status = 'Graded' AND OLD.status IN ('Submitted','AutoSubmitted')
           AND jsonb_array_length(NEW.paper_snapshot_json -> 'sections') > 0 THEN
            SELECT count(*) FILTER (WHERE grading_status = 'Pending'),
                   COALESCE(sum(score), 0), COALESCE(sum(max_score), 0)
            INTO v_pending, v_answer_score, v_answer_max
            FROM attempt_answer WHERE attempt_id = NEW.id;
            IF v_pending > 0 OR v_answer_score IS DISTINCT FROM NEW.auto_score
               OR v_answer_max IS DISTINCT FROM NEW.max_score THEN
                RAISE EXCEPTION 'Graded attempt requires complete per-question grading matching auto_score and max_score' USING ERRCODE = '23514';
            END IF;
        END IF;
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_exam_attempt_guard ON exam_attempt;
CREATE TRIGGER trg_exam_attempt_guard BEFORE INSERT OR UPDATE OR DELETE ON exam_attempt
FOR EACH ROW EXECUTE FUNCTION fn_guard_exam_attempt();

CREATE OR REPLACE FUNCTION fn_assert_attempt_answer(p_answer jsonb, p_type text, p_version_id bigint) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_keys text[];
BEGIN
    IF p_answer IS NULL OR p_answer = '{}'::jsonb THEN RETURN; END IF;
    IF jsonb_typeof(p_answer) <> 'object' THEN RAISE EXCEPTION 'attempt answer must be an object' USING ERRCODE = '23514'; END IF;
    SELECT array_agg(k.key ORDER BY k.key) INTO v_keys FROM jsonb_object_keys(p_answer) AS k(key);
    IF p_type = 'SingleChoice' THEN
        IF v_keys IS DISTINCT FROM ARRAY['value'] OR jsonb_typeof(p_answer -> 'value') <> 'string'
           OR NOT EXISTS (SELECT 1 FROM question_option WHERE question_version_id = p_version_id AND label = p_answer ->> 'value') THEN
            RAISE EXCEPTION 'invalid SingleChoice attempt answer' USING ERRCODE = '23514';
        END IF;
    ELSIF p_type = 'MultipleChoice' THEN
        IF v_keys IS DISTINCT FROM ARRAY['values'] OR jsonb_typeof(p_answer -> 'values') <> 'array'
           OR jsonb_array_length(p_answer -> 'values') < 1
           OR EXISTS (SELECT 1 FROM jsonb_array_elements_text(p_answer -> 'values') v GROUP BY v HAVING count(*) > 1)
           OR EXISTS (
               SELECT 1 FROM jsonb_array_elements_text(p_answer -> 'values') v
               WHERE NOT EXISTS (SELECT 1 FROM question_option o WHERE o.question_version_id = p_version_id AND o.label = v)
           ) THEN
            RAISE EXCEPTION 'invalid MultipleChoice attempt answer' USING ERRCODE = '23514';
        END IF;
    ELSIF p_type = 'TrueFalse' THEN
        IF v_keys IS DISTINCT FROM ARRAY['value'] OR jsonb_typeof(p_answer -> 'value') <> 'boolean' THEN
            RAISE EXCEPTION 'invalid TrueFalse attempt answer' USING ERRCODE = '23514';
        END IF;
    ELSE
        RAISE EXCEPTION 'unsupported question type' USING ERRCODE = '23514';
    END IF;
END $$;

CREATE OR REPLACE FUNCTION fn_guard_attempt_answer() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_status text;
    v_snapshot jsonb;
    v_type text;
    v_expected_max numeric(6,2);
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'attempt answers cannot be physically deleted' USING ERRCODE = '23514'; END IF;
    SELECT status, paper_snapshot_json INTO v_status, v_snapshot FROM exam_attempt WHERE id = NEW.attempt_id;
    SELECT q.type INTO v_type FROM question_version qv JOIN question q ON q.id = qv.question_id WHERE qv.id = NEW.question_version_id;
    IF TG_OP = 'INSERT' THEN
        IF v_status <> 'InProgress' THEN RAISE EXCEPTION 'answers can only be inserted while InProgress' USING ERRCODE = '23514'; END IF;
        IF NEW.score <> 0 OR NEW.grading_status <> 'Pending' THEN
            RAISE EXCEPTION 'new attempt answers must be pending with zero score' USING ERRCODE = '23514';
        END IF;
    ELSE
        IF v_status = 'InProgress' THEN
            IF NEW.question_version_id IS DISTINCT FROM OLD.question_version_id THEN
                RAISE EXCEPTION 'attempt answer question identity is immutable' USING ERRCODE = '23514';
            END IF;
            IF ROW(NEW.score, NEW.max_score, NEW.grading_status) IS DISTINCT FROM ROW(OLD.score, OLD.max_score, OLD.grading_status) THEN
                RAISE EXCEPTION 'candidate answers cannot change grading columns' USING ERRCODE = '23514';
            END IF;
        ELSIF v_status IN ('Submitted','AutoSubmitted') THEN
            IF ROW(NEW.question_version_id, NEW.answer_json) IS DISTINCT FROM ROW(OLD.question_version_id, OLD.answer_json)
               OR OLD.grading_status NOT IN ('Pending','Graded','Invalid') THEN
                RAISE EXCEPTION 'submitted answer content is frozen' USING ERRCODE = '23514';
            END IF;
            IF NEW.max_score IS DISTINCT FROM OLD.max_score THEN
                RAISE EXCEPTION 'attempt answer max_score is immutable' USING ERRCODE = '23514';
            END IF;
        ELSE
            RAISE EXCEPTION 'graded or voided answers are immutable' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF jsonb_array_length(v_snapshot -> 'sections') > 0 THEN
        SELECT (q.question ->> 'score')::numeric INTO v_expected_max
        FROM jsonb_array_elements(v_snapshot -> 'sections') AS s(section)
        CROSS JOIN LATERAL jsonb_array_elements(s.section -> 'questions') AS q(question)
        WHERE q.question ->> 'questionVersionId' = NEW.question_version_id::text;
        IF v_expected_max IS NULL OR NEW.max_score IS DISTINCT FROM v_expected_max THEN
            RAISE EXCEPTION 'answer question_version_id and max_score must match the attempt paper snapshot' USING ERRCODE = '23514';
        END IF;
    END IF;
    PERFORM fn_assert_attempt_answer(NEW.answer_json, v_type, NEW.question_version_id);
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_attempt_answer_guard ON attempt_answer;
CREATE TRIGGER trg_attempt_answer_guard BEFORE INSERT OR UPDATE OR DELETE ON attempt_answer
FOR EACH ROW EXECUTE FUNCTION fn_guard_attempt_answer();

-- Adjustment preconditions are checked against the current attempt; the row itself is append-only.
CREATE OR REPLACE FUNCTION fn_check_score_adjustment() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_attempt record;
    v_result_status text;
BEGIN
    SELECT a.status, a.score, a.max_score, t.result_status INTO v_attempt
    FROM exam_attempt a JOIN exam_assignment ass ON ass.id = a.assignment_id
    JOIN exam_task t ON t.id = ass.task_id
    WHERE a.id = NEW.attempt_id;
    IF v_attempt.status <> 'Graded' OR NEW.old_score IS DISTINCT FROM v_attempt.score
       OR NEW.new_score > v_attempt.max_score OR v_attempt.result_status = 'Published' THEN
        RAISE EXCEPTION 'adjustment requires Graded attempt, matching old_score, unpublished result and new_score within max_score' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_score_adjustment_check ON score_adjustment;
CREATE TRIGGER trg_score_adjustment_check BEFORE INSERT ON score_adjustment
FOR EACH ROW EXECUTE FUNCTION fn_check_score_adjustment();

-- Outbox delivery is retryable but final messages and their event identity are immutable.
CREATE OR REPLACE FUNCTION fn_guard_outbox_message() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'outbox messages cannot be physically deleted' USING ERRCODE = '23514'; END IF;
    IF TG_OP = 'INSERT' AND NEW.status <> 'Pending' THEN
        RAISE EXCEPTION 'outbox messages must be created as Pending' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'UPDATE' THEN
        IF OLD.status IN ('Sent','Dead') THEN
            RAISE EXCEPTION 'terminal outbox messages are immutable' USING ERRCODE = '23514';
        END IF;
        IF ROW(NEW.event_key, NEW.type, NEW.aggregate_type, NEW.aggregate_id, NEW.payload_json, NEW.created_at)
           IS DISTINCT FROM ROW(OLD.event_key, OLD.type, OLD.aggregate_type, OLD.aggregate_id, OLD.payload_json, OLD.created_at) THEN
            RAISE EXCEPTION 'outbox event identity and payload are immutable' USING ERRCODE = '23514';
        END IF;
        IF OLD.sent_at IS NOT NULL AND NEW.sent_at IS DISTINCT FROM OLD.sent_at THEN
            RAISE EXCEPTION 'outbox sent_at is immutable' USING ERRCODE = '23514';
        END IF;
        IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
            (OLD.status = 'Pending' AND NEW.status IN ('Processing','Sent','Dead'))
            OR (OLD.status = 'Processing' AND NEW.status IN ('Pending','Sent','Dead'))
        ) THEN
            RAISE EXCEPTION 'invalid outbox state transition' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_outbox_message_guard ON outbox_message;
CREATE TRIGGER trg_outbox_message_guard BEFORE INSERT OR UPDATE OR DELETE ON outbox_message
FOR EACH ROW EXECUTE FUNCTION fn_guard_outbox_message();

-- Append-only facts. These triggers also protect the table owner.
CREATE OR REPLACE FUNCTION fn_reject_append_only_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME USING ERRCODE = '23514';
END $$;

DO $append_only$
DECLARE
    v_table text;
BEGIN
    FOREACH v_table IN ARRAY ARRAY['audit_event','attempt_event','score_adjustment','retake_grant','revocation_record',
                                   'result_release','result_release_item','report_export'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS trg_%1$s_append_only ON %1$s', v_table);
        EXECUTE format('CREATE TRIGGER trg_%1$s_append_only BEFORE UPDATE OR DELETE ON %1$s FOR EACH ROW EXECUTE FUNCTION fn_reject_append_only_mutation()', v_table);
        EXECUTE format('DROP TRIGGER IF EXISTS trg_%1$s_no_truncate ON %1$s', v_table);
        EXECUTE format('CREATE TRIGGER trg_%1$s_no_truncate BEFORE TRUNCATE ON %1$s FOR EACH STATEMENT EXECUTE FUNCTION fn_reject_append_only_mutation()', v_table);
    END LOOP;
END $append_only$;

CREATE OR REPLACE FUNCTION fn_guard_schema_meta_singleton() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'schema_meta is a singleton row and cannot be deleted' USING ERRCODE = '23514';
    END IF;
    IF NEW.id <> 1 THEN
        RAISE EXCEPTION 'schema_meta is a singleton row with id=1' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_schema_meta_singleton ON schema_meta;
CREATE TRIGGER trg_schema_meta_singleton BEFORE INSERT OR UPDATE OR DELETE ON schema_meta
FOR EACH ROW EXECUTE FUNCTION fn_guard_schema_meta_singleton();

COMMENT ON TABLE schema_meta IS 'Singleton id=1. Legacy key/value/updated_at columns are retained only for compatibility and unused.';
DO $legacy_comments$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'exam_task'::regclass
               AND attname = 'retake_deadline' AND NOT attisdropped) THEN
        EXECUTE 'COMMENT ON COLUMN exam_task.retake_deadline IS ''Deprecated legacy local timestamp; use retake_deadline_at timestamptz.''';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'outbox_message'::regclass
               AND attname = 'completed_at' AND NOT attisdropped) THEN
        EXECUTE 'COMMENT ON COLUMN outbox_message.completed_at IS ''Deprecated; use sent_at.''';
    END IF;
END $legacy_comments$;
COMMENT ON TABLE audit_event IS 'Append-only. Application role has INSERT/SELECT only; triggers reject UPDATE/DELETE/TRUNCATE.';
COMMENT ON TABLE attempt_event IS 'Append-only attempt events; no UPDATE/DELETE/TRUNCATE.';
COMMENT ON TABLE score_adjustment IS 'Append-only adjustment facts; exam_attempt.score stores the current adjusted total.';
COMMENT ON TABLE result_release IS 'Append-only result publication headers; payload_sha256 covers the immutable release items.';
COMMENT ON TABLE result_release_item IS 'Append-only per-assignment final score snapshots; NULL means no valid attempt, never a fabricated zero.';
COMMENT ON TABLE report_export IS 'Append-only export trace; file_key is relative to Data/export and expires_at controls download availability.';
COMMENT ON TABLE retake_grant IS 'Append-only quota grants.';
COMMENT ON TABLE revocation_record IS 'Append-only revocation and void facts.';
COMMENT ON COLUMN department.path IS 'Materialized path such as 001.003.; every segment is exactly three digits.';
COMMENT ON COLUMN department.path_segment IS 'Stable three-digit child segment; unique within parent.';
COMMENT ON COLUMN exam_assignment.status IS 'Active uniqueness excludes Revoked so a reissued assignment can coexist with history.';
COMMENT ON COLUMN exam_assignment.inherited_attempts_used IS 'Historical quota consumed before reissue; effective used = attempts_used + inherited_attempts_used.';
COMMENT ON COLUMN exam_attempt.start_key IS 'Globally unique domain idempotency key. Legacy rows use legacy-attempt-{id}.';
COMMENT ON COLUMN exam_attempt.auto_score IS 'Immutable automatic grading fact; score may change only through appended adjustments.';
COMMENT ON COLUMN exam_attempt.paper_snapshot_json IS 'Actual start-time paper snapshot; contains no standard answers.';
COMMENT ON COLUMN question.type IS 'SingleChoice / MultipleChoice / TrueFalse only.';
COMMENT ON COLUMN import_row.raw_json IS 'Preview staging content; BLL must scrub full stems/options before terminal batch status.';

COMMIT;
