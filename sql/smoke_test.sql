-- ExamOps smoke_test.sql
-- Run after schema.sql, seed.sql and security.sql:
--   psql -v ON_ERROR_STOP=1 -d examops_db -f sql/smoke_test.sql
-- Every assertion and fixture is rolled back. No application or database rebuild is performed.

BEGIN;

DO $smoke$
DECLARE
    v_root bigint;
    v_child bigint;
    v_grandchild bigint;
    v_user bigint;
    v_manager bigint;
    v_bank bigint;
    v_question bigint;
    v_version bigint;
    v_bad_version bigint;
    v_category bigint;
    v_child_category bigint;
    v_other_category bigint;
    v_other_bank bigint;
    v_paper bigint;
    v_paper_version bigint;
    v_other_paper bigint;
    v_other_version bigint;
    v_section bigint;
    v_random_section bigint;
    v_paper_question bigint;
    v_task bigint;
    v_batch bigint;
    v_batch_row bigint;
    v_assignment bigint;
    v_old_assignment bigint;
    v_attempt bigint;
    v_answer bigint;
    v_audit bigint;
    v_event bigint;
    v_import_batch bigint;
    v_import_question bigint;
    v_import_version bigint;
    v_count integer;
    v_snapshot jsonb;
    v_token text := substr(md5(random()::text || clock_timestamp()::text), 1, 12);
    v_root_segment text;
    v_root_path text;
    v_child_path text;
    v_grandchild_path text;
    v_user_name text;
    v_manager_name text;
    v_p4_user bigint;
    v_p4_task bigint;
    v_p4_assignment bigint;
    v_p4_auto_attempt bigint;
    v_p4_force_attempt bigint;
    v_p4_void_attempt bigint;
    v_p4_void_answer bigint;
    v_p4_revocation bigint;
    v_p4_outbox_retry bigint;
    v_p4_outbox_sent bigint;
    v_p4_outbox_stale bigint;
    v_p4_now timestamptz := timestamptz '2030-01-01 09:00:00+00';
    v_p4_deadline timestamptz := timestamptz '2030-01-01 09:30:00+00';
    v_p4_next timestamptz;
    v_p4_snapshot jsonb;
    v_p4_stored_snapshot jsonb;
    v_p4_before jsonb;
    v_p4_after jsonb;
    v_p4_row record;
    v_p4_rejected boolean;
    v_p4_i integer;
    v_p4_delay integer;
    v_p5_release bigint;
    v_p5_release_withdrawn bigint;
    v_p5_item bigint;
    v_p5_export bigint;
    v_p5_task bigint;
    v_p5_assignment bigint;
    v_p5_attempt bigint;
BEGIN
    -- Unique fixtures keep the smoke suite safe against a non-empty database.
    LOOP
        v_root_segment := lpad((floor(random() * 900 + 100))::integer::text, 3, '0');
        v_root_path := v_root_segment || '.';
        EXIT WHEN NOT EXISTS (SELECT 1 FROM department WHERE path LIKE v_root_segment || '.%');
    END LOOP;
    v_child_path := v_root_path || '001.';
    v_grandchild_path := v_child_path || '001.';
    v_user_name := 'smoke_candidate_' || v_token;
    v_manager_name := 'smoke_manager_' || v_token;

    -- Department paths: exactly three digits, parent prefix, stable segment and no cycles.
    INSERT INTO department(parent_id, name, path, path_segment, sort_order)
    VALUES (NULL, '冒烟根部门', v_root_path, v_root_segment, 0) RETURNING id INTO v_root;
    INSERT INTO department(parent_id, name, path, path_segment, sort_order)
    VALUES (v_root, '冒烟子部门', v_child_path, '001', 1) RETURNING id INTO v_child;
    INSERT INTO department(parent_id, name, path, path_segment, sort_order)
    VALUES (v_child, '冒烟孙部门', v_grandchild_path, '001', 1) RETURNING id INTO v_grandchild;

    SELECT count(*) INTO v_count FROM department WHERE path LIKE v_root_segment || '.%';
    IF v_count <> 3 THEN RAISE EXCEPTION 'department subtree assertion failed: %', v_count; END IF;

    BEGIN
        INSERT INTO department(parent_id, name, path, path_segment) VALUES (v_root, '非法路径段', v_root_path || '01.', '01');
        RAISE EXCEPTION 'two-digit path segment was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO department(parent_id, name, path, path_segment) VALUES (v_root, '错误父路径', v_root_path || '002.', '001');
        RAISE EXCEPTION 'mismatched parent path was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE department SET parent_id = v_grandchild, path = v_grandchild_path || v_root_path WHERE id = v_root;
        RAISE EXCEPTION 'department cycle was not rejected';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO department(parent_id, name, path, path_segment) VALUES (v_root, '重复路径段', v_child_path, '001');
        RAISE EXCEPTION 'duplicate sibling path_segment was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    INSERT INTO app_user(username, display_name, password_hash, status, primary_department_id)
    VALUES (v_user_name, '冒烟考生', '', 'Active', v_child) RETURNING id INTO v_user;
    INSERT INTO app_user(username, display_name, password_hash, status, primary_department_id)
    VALUES (v_manager_name, '冒烟考务', '', 'Active', v_root) RETURNING id INTO v_manager;
    INSERT INTO user_department(user_id, department_id, is_primary) VALUES (v_user, v_child, true);

    -- Role scope anchors: All/Self/Assigned use NULL; department scopes require a department.
    BEGIN
        INSERT INTO user_role(user_id, role_id, department_id)
        SELECT v_user, id, v_child FROM role WHERE code = 'Candidate';
        RAISE EXCEPTION 'Self role accepted a department anchor';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    INSERT INTO user_role(user_id, role_id, department_id)
    SELECT v_user, id, NULL FROM role WHERE code = 'Candidate';
    BEGIN
        INSERT INTO user_role(user_id, role_id, department_id)
        SELECT v_manager, id, NULL FROM role WHERE code = 'DeptAdmin';
        RAISE EXCEPTION 'department-scoped role accepted NULL anchor';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    INSERT INTO user_role(user_id, role_id, department_id)
    SELECT v_manager, id, v_root FROM role WHERE code = 'DeptAdmin';
    BEGIN
        UPDATE role SET code = 'TamperedAdmin' WHERE code = 'PlatformAdmin';
        RAISE EXCEPTION 'system role code was changed';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;
    BEGIN
        DELETE FROM role WHERE code = 'PlatformAdmin';
        RAISE EXCEPTION 'system role was deleted';
    EXCEPTION WHEN check_violation OR foreign_key_violation OR insufficient_privilege THEN NULL;
    END;

    -- Question type, real answer shape and option agreement.
    INSERT INTO question_bank(name, department_id) VALUES ('冒烟题库', v_root) RETURNING id INTO v_bank;
    BEGIN
        INSERT INTO question(bank_id, external_code, type) VALUES (v_bank, 'SMOKE-BLANK', 'FillBlank');
        RAISE EXCEPTION 'unsupported question type was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    INSERT INTO question(bank_id, external_code, type)
    VALUES (v_bank, 'SMOKE-Q1', 'SingleChoice') RETURNING id INTO v_question;
    INSERT INTO question_version(question_id, version_no, stem, answer_json, difficulty, default_score)
    VALUES (v_question, 1, '冒烟题干', '{"value":"A"}'::jsonb, 'Easy', 5) RETURNING id INTO v_version;
    INSERT INTO question_option(question_version_id, label, content, is_correct, sort_order)
    VALUES (v_version, 'A', '正确项', true, 1), (v_version, 'B', '错误项', false, 2);
    UPDATE question_version SET status = 'Published', published_at = now(), published_by = v_manager WHERE id = v_version;
    UPDATE question SET current_version_id = v_version, status = 'Published' WHERE id = v_question;

    BEGIN
        UPDATE question_version SET stem = '篡改已发布题干' WHERE id = v_version;
        RAISE EXCEPTION 'published question version was updated';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE question_option SET content = '篡改选项' WHERE question_version_id = v_version AND label = 'A';
        RAISE EXCEPTION 'published question option was updated';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO question_option(question_version_id, label, content, sort_order)
        VALUES (v_version, 'C', ' published child insert', 3);
        RAISE EXCEPTION 'published question option was inserted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    INSERT INTO question_tag(name) VALUES ('冒烟标签-' || v_token);
    BEGIN
        INSERT INTO question_tag_link(question_version_id, tag_id)
        SELECT v_version, id FROM question_tag WHERE name = '冒烟标签-' || v_token;
        RAISE EXCEPTION 'published question tag link was inserted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    INSERT INTO question(bank_id, external_code, type) VALUES (v_bank, 'SMOKE-BAD-MULTI', 'MultipleChoice');
    INSERT INTO question_version(question_id, version_no, stem, answer_json, default_score, published_at)
    SELECT id, 1, '答案与选项不一致', '{"values":["A"]}'::jsonb, 5, now()
    FROM question WHERE bank_id = v_bank AND external_code = 'SMOKE-BAD-MULTI' RETURNING id INTO v_bad_version;
    INSERT INTO question_option(question_version_id, label, content, is_correct, sort_order)
    VALUES (v_bad_version, 'A', '甲', true, 1), (v_bad_version, 'B', '乙', true, 2);
    BEGIN
        UPDATE question_version SET status = 'Published' WHERE id = v_bad_version;
        RAISE EXCEPTION 'inconsistent MultipleChoice answer was published';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO question_version(question_id, version_no, stem, answer_json, default_score)
        SELECT id, 2, '零分题', '{"value":"A"}'::jsonb, 0 FROM question WHERE id = v_question;
        RAISE EXCEPTION 'default_score <= 0 was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Category tree is bank-scoped and published links are immutable.
    INSERT INTO question_category(bank_id, name) VALUES (v_bank, '根分类') RETURNING id INTO v_category;
    INSERT INTO question_category(bank_id, parent_id, name) VALUES (v_bank, v_category, '子分类') RETURNING id INTO v_child_category;
    INSERT INTO question_category(bank_id, name) VALUES (v_bank, '其他分类') RETURNING id INTO v_other_category;
    INSERT INTO question_bank(name, department_id) VALUES ('其他冒烟题库', v_root) RETURNING id INTO v_other_bank;
    INSERT INTO question_category(bank_id, name) VALUES (v_other_bank, '跨库分类') RETURNING id INTO v_other_category;
    BEGIN
        INSERT INTO question_category(bank_id, parent_id, name) VALUES (v_bank, v_other_category, '跨库父分类');
        RAISE EXCEPTION 'cross-bank category parent was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO question_category(bank_id, name) VALUES (v_bank, '根分类');
        RAISE EXCEPTION 'duplicate root category name was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO question_category_link(question_version_id, category_id) VALUES (v_bad_version, v_other_category);
        RAISE EXCEPTION 'cross-bank category link was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO question_category_link(question_version_id, category_id) VALUES (v_version, v_category);
        RAISE EXCEPTION 'published category link was inserted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    INSERT INTO question_category_link(question_version_id, category_id) VALUES (v_bad_version, v_category);
    UPDATE question_category_link SET category_id = v_child_category
    WHERE question_version_id = v_bad_version AND category_id = v_category;
    IF NOT EXISTS (SELECT 1 FROM question_category_link WHERE question_version_id = v_bad_version AND category_id = v_child_category) THEN
        RAISE EXCEPTION 'draft category link could not be updated';
    END IF;

    -- Paper structure: Fixed rules, same-version section, published question reference and immutable children.
    INSERT INTO paper(title, department_id) VALUES ('冒烟试卷', v_root) RETURNING id INTO v_paper;
    INSERT INTO paper_version(paper_id, version_no, title, total_score, duration_minutes)
    VALUES (v_paper, 1, '冒烟试卷 V1', 5, 30) RETURNING id INTO v_paper_version;
    INSERT INTO paper_section(paper_version_id, title, sort_order, selection_mode, pick_count, score_per_question)
    VALUES (v_paper_version, '单项选择题', 1, 'Fixed', NULL, 5) RETURNING id INTO v_section;
    INSERT INTO paper_question(paper_version_id, section_id, question_version_id, sort_order, score, is_pool)
    VALUES (v_paper_version, v_section, v_version, 1, 5, false) RETURNING id INTO v_paper_question;

    BEGIN
        INSERT INTO paper_section(paper_version_id, title, sort_order, selection_mode, pick_count, score_per_question)
        VALUES (v_paper_version, '非法Fixed', 2, 'Fixed', 1, 5);
        RAISE EXCEPTION 'Fixed section accepted pick_count';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    UPDATE paper_version SET status = 'Published', published_at = now(), published_by = v_manager WHERE id = v_paper_version;
    UPDATE paper SET current_version_id = v_paper_version, status = 'Published' WHERE id = v_paper;
    BEGIN
        UPDATE paper SET status = 'Draft' WHERE id = v_paper;
        RAISE EXCEPTION 'published paper regressed to Draft';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    BEGIN
        UPDATE paper_section SET title = '篡改已发布小节' WHERE id = v_section;
        RAISE EXCEPTION 'published paper section was updated';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        DELETE FROM paper_question WHERE id = v_paper_question;
        RAISE EXCEPTION 'published paper question was deleted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    INSERT INTO paper(title, department_id) VALUES ('其他试卷', v_root) RETURNING id INTO v_other_paper;
    INSERT INTO paper_version(paper_id, version_no, title, total_score, duration_minutes)
    VALUES (v_other_paper, 1, '其他试卷V1', 0, 30) RETURNING id INTO v_other_version;
    BEGIN
        UPDATE paper SET current_version_id = v_other_version WHERE id = v_paper;
        RAISE EXCEPTION 'paper current_version_id crossed parent objects';
    EXCEPTION WHEN check_violation OR foreign_key_violation THEN NULL;
    END;
    INSERT INTO paper_section(paper_version_id, title, sort_order, selection_mode, score_per_question)
    VALUES (v_other_version, '其他小节', 1, 'Fixed', 5);
    BEGIN
        INSERT INTO paper_question(paper_version_id, section_id, question_version_id, sort_order, score, is_pool)
        SELECT v_paper_version, id, v_version, 9, 5, false FROM paper_section WHERE paper_version_id = v_other_version;
        RAISE EXCEPTION 'paper question section crossed versions';
    EXCEPTION WHEN check_violation OR foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO paper_section(paper_version_id, title, sort_order, selection_mode, pick_count, score_per_question)
        VALUES (v_other_version, '非法Fixed', 2, 'Fixed', 1, 5);
        RAISE EXCEPTION 'Fixed section accepted pick_count';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    INSERT INTO paper_section(paper_version_id, title, sort_order, selection_mode, pick_count, score_per_question,
                              bank_id, category_id, question_type, difficulty)
    VALUES (v_other_version, '随机抽题', 2, 'Random', 1, 5, v_bank, v_category, 'SingleChoice', 'Easy')
    RETURNING id INTO v_random_section;
    IF NOT EXISTS (SELECT 1 FROM paper_section WHERE id = v_random_section AND bank_id = v_bank
                   AND category_id = v_category AND question_type = 'SingleChoice' AND difficulty = 'Easy') THEN
        RAISE EXCEPTION 'random blueprint columns did not round-trip';
    END IF;
    BEGIN
        UPDATE paper_section SET question_type = 'FillBlank' WHERE id = v_random_section;
        RAISE EXCEPTION 'unsupported blueprint question type was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE paper_section SET difficulty = 'Impossible' WHERE id = v_random_section;
        RAISE EXCEPTION 'unsupported blueprint difficulty was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE paper_section SET category_id = v_other_category WHERE id = v_random_section;
        RAISE EXCEPTION 'cross-bank blueprint category was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO paper_question(paper_version_id, section_id, question_version_id, sort_order, score, is_pool)
        SELECT v_other_version, id, v_version, 1, 5, false FROM paper_section
        WHERE paper_version_id = v_other_version AND selection_mode = 'Random';
        RAISE EXCEPTION 'Random section accepted is_pool=false';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    INSERT INTO paper_question(paper_version_id, section_id, question_version_id, sort_order, score, is_pool)
    SELECT v_other_version, id, v_version, 1, 5, true FROM paper_section
    WHERE paper_version_id = v_other_version AND selection_mode = 'Random';
    BEGIN
        UPDATE paper_section SET selection_mode = 'Fixed', pick_count = NULL
        WHERE paper_version_id = v_other_version AND selection_mode = 'Random';
        RAISE EXCEPTION 'section mode diverged from existing pool flags';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Draft task may omit a paper; publication locks department, schedule and rules.
    INSERT INTO exam_task(department_id, code, title, exam_date, available_from, available_to, created_by)
    VALUES (v_root, 'SMOKE-TASK-' || v_token, '冒烟考试', DATE '2030-01-01',
            timestamp '2030-01-01 09:00:00', timestamp '2030-01-01 10:00:00', v_manager)
    RETURNING id INTO v_task;
    UPDATE exam_task SET paper_version_id = v_paper_version, status = 'Published', published_at = now() WHERE id = v_task;
    BEGIN
        UPDATE exam_task SET status = 'Draft' WHERE id = v_task;
        RAISE EXCEPTION 'published task regressed to Draft';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE exam_task SET duration_minutes = 90 WHERE id = v_task;
        RAISE EXCEPTION 'published task rule was changed';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    -- A Published result without an immutable release pointer is rejected.
    BEGIN
        UPDATE exam_task SET result_status = 'Published', result_published_at = now() WHERE id = v_task;
        RAISE EXCEPTION 'published result without result_release_id was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE exam_task SET result_status = 'Withdrawn', result_published_at = now(),
            result_withdrawn_at = now(), result_withdraw_reason = NULL WHERE id = v_task;
        RAISE EXCEPTION 'withdrawn result without a reason was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Batch progress has durable row details and terminal counts must balance.
    INSERT INTO assignment_batch(task_id, idempotency_key, target_json, total_count, created_by)
    VALUES (v_task, v_token || '-batch-key', '{"type":"user"}'::jsonb, 1, v_manager) RETURNING id INTO v_batch;
    BEGIN
        UPDATE assignment_batch SET status = 'Completed', finished_at = now() WHERE id = v_batch;
        RAISE EXCEPTION 'unbalanced completed batch was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    UPDATE assignment_batch SET status = 'Running', started_at = now() WHERE id = v_batch;
    INSERT INTO assignment_batch_row(batch_id, row_no, user_id, target_json)
    VALUES (v_batch, 1, v_user, '{"userId":"smoke"}'::jsonb) RETURNING id INTO v_batch_row;

    INSERT INTO exam_assignment(batch_id, task_id, user_id, department_id_snapshot, status,
                                available_from, available_to, duration_minutes_override)
    VALUES (v_batch, v_task, v_user, v_child, 'Pending',
            timestamp '2030-01-01 09:00:00', timestamp '2030-01-01 10:00:00', 45)
    RETURNING id INTO v_old_assignment;
    BEGIN
        INSERT INTO exam_assignment(batch_id, task_id, user_id, department_id_snapshot, status, available_from, available_to)
        VALUES (v_batch, v_task, v_user, v_child, 'Pending',
                timestamp '2030-01-01 09:00:00', timestamp '2030-01-01 10:00:00');
        RAISE EXCEPTION 'duplicate active assignment was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    UPDATE exam_assignment SET status = 'Revoked', revoked_at = now(), revoke_reason = 'smoke revocation reason'
    WHERE id = v_old_assignment;
    INSERT INTO exam_assignment(batch_id, task_id, user_id, department_id_snapshot, status,
                                available_from, available_to, inherited_attempts_used, reissued_from_id)
    VALUES (v_batch, v_task, v_user, v_child, 'Pending',
            timestamp '2030-01-01 09:00:00', timestamp '2030-01-01 10:00:00', 1, v_old_assignment)
    RETURNING id INTO v_assignment;
    UPDATE exam_assignment SET status = 'Available' WHERE id = v_assignment;
    UPDATE assignment_batch_row SET status = 'Processing' WHERE id = v_batch_row;
    UPDATE assignment_batch_row SET status = 'Success', assignment_id = v_assignment WHERE id = v_batch_row;
    UPDATE assignment_batch SET status = 'Completed', success_count = 1, finished_at = now() WHERE id = v_batch;
    BEGIN
        UPDATE assignment_batch SET success_count = 0 WHERE id = v_batch;
        RAISE EXCEPTION 'terminal assignment batch was rewritten';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    SELECT count(*) INTO v_count FROM exam_assignment WHERE task_id = v_task AND user_id = v_user;
    IF v_count <> 2 THEN RAISE EXCEPTION 'reissue uniqueness assertion failed: %', v_count; END IF;

    -- Start/submit keys, real answer JSON, frozen submission and immutable auto score.
    v_snapshot := jsonb_build_object(
        'paperVersionId', to_jsonb(v_paper_version::text),
        'sections', jsonb_build_array(jsonb_build_object(
            'sectionId', to_jsonb(v_section::text), 'title', '单项选择题',
            'questions', jsonb_build_array(jsonb_build_object(
                'paperQuestionId', to_jsonb(v_paper_question::text),
                'questionVersionId', to_jsonb(v_version::text), 'sortOrder', 1, 'score', '5.00'
            ))
        ))
    );
    INSERT INTO exam_attempt(assignment_id, attempt_no, start_key, paper_snapshot_json,
                             started_at, deadline_at, max_score)
    VALUES (v_assignment, 1, v_token || '-start-key', v_snapshot, now(), now() + interval '30 minutes', 5)
    RETURNING id INTO v_attempt;
    BEGIN
        INSERT INTO exam_attempt(assignment_id, attempt_no, start_key, paper_snapshot_json, started_at, deadline_at, max_score)
        VALUES (v_assignment, 2, v_token || '-start-key', v_snapshot, now(), now() + interval '30 minutes', 5);
        RAISE EXCEPTION 'duplicate start_key was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO exam_attempt(assignment_id, attempt_no, start_key, paper_snapshot_json, started_at, deadline_at, max_score)
        VALUES (v_assignment, 2, v_token || '-second-start', v_snapshot, now(), now() + interval '30 minutes', 5);
        RAISE EXCEPTION 'second InProgress attempt was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    INSERT INTO attempt_answer(attempt_id, question_version_id, answer_json, score, max_score)
    VALUES (v_attempt, v_version, '{"value":"A"}'::jsonb, 0, 5) RETURNING id INTO v_answer;
    BEGIN
        UPDATE attempt_answer SET answer_json = '{"selected":"A"}'::jsonb WHERE id = v_answer;
        RAISE EXCEPTION 'legacy selected answer shape was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO attempt_answer(attempt_id, question_version_id, answer_json)
        VALUES (v_attempt, v_bad_version, '{"values":["A"]}'::jsonb);
        RAISE EXCEPTION 'answer outside paper snapshot was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO attempt_answer(attempt_id, question_version_id, answer_json, score, max_score)
        VALUES (v_attempt, v_version, '{"value":"B"}'::jsonb, 0, 5);
        RAISE EXCEPTION 'duplicate attempt answer was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    UPDATE exam_attempt SET status = 'Submitted', submit_key = v_token || '-submit-key', submitted_at = now(), is_late = false
    WHERE id = v_attempt;
    BEGIN
        UPDATE attempt_answer SET answer_json = '{"value":"B"}'::jsonb WHERE id = v_answer;
        RAISE EXCEPTION 'submitted answer content was changed';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE attempt_answer SET score = 2, grading_status = 'Graded' WHERE id = v_answer;
        RAISE EXCEPTION 'partial objective score was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    UPDATE attempt_answer SET score = 5, grading_status = 'Graded' WHERE id = v_answer;
    BEGIN
        UPDATE exam_attempt SET status = 'Graded', auto_score = 4, score = 4, graded_at = now() WHERE id = v_attempt;
        RAISE EXCEPTION 'attempt auto_score disagreed with per-question grading';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    UPDATE exam_attempt SET status = 'Graded', auto_score = 5, score = 5, graded_at = now() WHERE id = v_attempt;
    BEGIN
        UPDATE exam_attempt SET auto_score = 4 WHERE id = v_attempt;
        RAISE EXCEPTION 'immutable auto_score was changed';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE exam_attempt SET score = 6 WHERE id = v_attempt;
        RAISE EXCEPTION 'attempt score exceeded max_score';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    INSERT INTO score_adjustment(attempt_id, old_score, new_score, reason, adjusted_by, idempotency_key)
    VALUES (v_attempt, 5, 4, 'smoke approved score adjustment', v_manager, v_token || '-adjust-key');
    UPDATE exam_attempt SET score = 4 WHERE id = v_attempt;
    BEGIN
        INSERT INTO score_adjustment(attempt_id, old_score, new_score, reason, adjusted_by, idempotency_key)
        VALUES (v_attempt, 5, 3, 'smoke stale old score', v_manager, v_token || '-adjust-stale');
        RAISE EXCEPTION 'adjustment with stale old_score was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO score_adjustment(attempt_id, old_score, new_score, reason, adjusted_by, idempotency_key)
        VALUES (v_attempt, 4, 6, 'smoke adjustment above maximum', v_manager, v_token || '-adjust-high');
        RAISE EXCEPTION 'adjustment above max_score was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Append-only facts, domain idempotency and audit result references.
    INSERT INTO attempt_event(attempt_id, actor_id, event_type, payload_json, event_key)
    VALUES (v_attempt, v_user, 'AnswerSaved', '{"questionVersionId":"smoke"}'::jsonb, v_token || '-event-key')
    RETURNING id INTO v_event;
    BEGIN
        INSERT INTO attempt_event(attempt_id, actor_id, event_type, event_key)
        VALUES (v_attempt, v_user, 'AnswerSaved', v_token || '-event-key');
        RAISE EXCEPTION 'duplicate attempt event key was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        UPDATE attempt_event SET event_type = 'Tampered' WHERE id = v_event;
        RAISE EXCEPTION 'attempt_event update was not rejected';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;
    BEGIN
        DELETE FROM attempt_event WHERE id = v_event;
        RAISE EXCEPTION 'attempt_event delete was not rejected';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;
    BEGIN
        TRUNCATE TABLE attempt_event;
        RAISE EXCEPTION 'attempt_event truncate was not rejected';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;

    INSERT INTO audit_event(actor_id, actor_role, action, entity_type, entity_id, request_id, trace_id,
                            idempotency_key, request_hash, http_method, route_template, result_ref_json)
    VALUES (v_user, 'Candidate', 'attempt.submit', 'ExamAttempt', v_attempt, 'smoke-request', 'smoke-trace',
            v_token || '-api-key', repeat('a', 64), 'POST', '/api/attempts/{attemptId}/submit',
            jsonb_build_object('entityType','ExamAttempt','entityId',v_attempt::text))
    RETURNING id INTO v_audit;
    BEGIN
        INSERT INTO audit_event(actor_id, actor_role, action, entity_type, entity_id, idempotency_key, request_hash,
                                http_method, route_template)
        VALUES (v_user, 'Candidate', 'attempt.submit', 'ExamAttempt', v_attempt, v_token || '-api-key', repeat('a', 64),
                'POST', '/api/attempts/{attemptId}/submit');
        RAISE EXCEPTION 'duplicate scoped API idempotency record was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        UPDATE audit_event SET action = 'tampered' WHERE id = v_audit;
        RAISE EXCEPTION 'audit update was not rejected';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;
    BEGIN
        DELETE FROM audit_event WHERE id = v_audit;
        RAISE EXCEPTION 'audit delete was not rejected';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;
    BEGIN
        TRUNCATE TABLE audit_event;
        RAISE EXCEPTION 'audit truncate was not rejected';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;
    INSERT INTO audit_event(actor_id, actor_role, action, entity_type, entity_id, event_key)
    VALUES (NULL, 'System', 'attempt.auto_submit', 'ExamAttempt', v_attempt, v_token || '-system-audit-key');

    INSERT INTO retake_grant(assignment_id, extra_attempts, reason, granted_by, idempotency_key)
    VALUES (v_assignment, 1, 'smoke retake grant', v_manager, v_token || '-retake-key');
    BEGIN
        UPDATE retake_grant SET extra_attempts = 2 WHERE assignment_id = v_assignment;
        RAISE EXCEPTION 'retake_grant was mutated';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;
    INSERT INTO revocation_record(target_type, target_id, scope, reason, actor_id, idempotency_key)
    VALUES ('ExamAssignment', v_old_assignment, 'KeepScore', 'smoke revocation record reason', v_manager, v_token || '-revoke-key');
    BEGIN
        DELETE FROM revocation_record WHERE target_id = v_old_assignment;
        RAISE EXCEPTION 'revocation_record was deleted';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;

    INSERT INTO outbox_message(event_key, type, aggregate_type, aggregate_id, payload_json)
    VALUES (v_token || '-outbox-key', 'AttemptGraded', 'ExamAttempt', v_attempt, '{"attemptId":"smoke"}'::jsonb);
    BEGIN
        INSERT INTO outbox_message(event_key, type, aggregate_type, aggregate_id)
        VALUES (v_token || '-outbox-key', 'AttemptGraded', 'ExamAttempt', v_attempt);
        RAISE EXCEPTION 'duplicate outbox event_key was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO outbox_message(event_key, type, aggregate_type, aggregate_id, status)
        VALUES (v_token || '-outbox-invalid', 'AttemptGraded', 'ExamAttempt', v_attempt, 'Error');
        RAISE EXCEPTION 'legacy outbox Error status was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO outbox_message(event_key, type, aggregate_type, aggregate_id, status)
        VALUES (v_token || '-outbox-unsent', 'AttemptGraded', 'ExamAttempt', v_attempt, 'Sent');
        RAISE EXCEPTION 'Sent outbox without sent_at was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    UPDATE outbox_message SET status = 'Sent', sent_at = now() WHERE event_key = v_token || '-outbox-key';
    BEGIN
        UPDATE outbox_message SET status = 'Pending' WHERE event_key = v_token || '-outbox-key';
        RAISE EXCEPTION 'terminal outbox message was reopened';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Phase 4 fixtures are independent of the Graded attempt and Sent outbox above.
    INSERT INTO app_user(username, display_name, password_hash, status, primary_department_id)
    VALUES ('smoke_p4_candidate_' || v_token, 'Phase4 冒烟考生', '', 'Active', v_child)
    RETURNING id INTO v_p4_user;
    INSERT INTO user_department(user_id, department_id, is_primary) VALUES (v_p4_user, v_child, true);
    INSERT INTO exam_task(department_id, code, title, exam_date, available_from, available_to, created_by)
    VALUES (v_root, 'SMOKE-P4-' || v_token, 'Phase4 冒烟考试', DATE '2030-01-01',
            timestamp '2030-01-01 09:00:00', timestamp '2030-01-01 10:00:00', v_manager)
    RETURNING id INTO v_p4_task;
    UPDATE exam_task SET paper_version_id = v_paper_version, status = 'Published', published_at = v_p4_now
    WHERE id = v_p4_task;
    INSERT INTO exam_assignment(task_id, user_id, department_id_snapshot, status, available_from, available_to)
    VALUES (v_p4_task, v_p4_user, v_child, 'Pending',
            timestamp '2030-01-01 09:00:00', timestamp '2030-01-01 10:00:00')
    RETURNING id INTO v_p4_assignment;
    UPDATE exam_assignment SET status = 'Available' WHERE id = v_p4_assignment;

    -- Assigned is a user_role scope; exam_task_staff stores only concrete staff roles.
    INSERT INTO user_role(user_id, role_id, department_id)
    SELECT v_p4_user, id, NULL FROM role WHERE code = 'Grader';
    IF NOT EXISTS (
        SELECT 1 FROM user_role ur JOIN role r ON r.id = ur.role_id
        WHERE ur.user_id = v_p4_user AND r.code = 'Grader' AND r.scope_type = 'Assigned' AND ur.department_id IS NULL
    ) THEN
        RAISE EXCEPTION 'Assigned role scope anchor must be NULL';
    END IF;
    INSERT INTO exam_task_staff(task_id, user_id, staff_role, assigned_by)
    VALUES (v_p4_task, v_p4_user, 'Grader', v_manager);
    v_p4_rejected := false;
    BEGIN
        INSERT INTO exam_task_staff(task_id, user_id, staff_role, assigned_by)
        VALUES (v_p4_task, v_p4_user, 'Assigned', v_manager);
        RAISE EXCEPTION 'Assigned was accepted as an exam_task_staff.staff_role' USING ERRCODE = 'P0001';
    EXCEPTION WHEN SQLSTATE '23514' THEN v_p4_rejected := true;
    END;
    IF NOT v_p4_rejected THEN RAISE EXCEPTION 'invalid staff_role did not raise SQLSTATE 23514'; END IF;
    v_p4_rejected := false;
    BEGIN
        INSERT INTO exam_task_staff(task_id, user_id, staff_role, assigned_by)
        VALUES (v_p4_task, v_p4_user, 'Grader', v_manager);
        RAISE EXCEPTION 'duplicate task/user/staff_role assignment was accepted' USING ERRCODE = 'P0001';
    EXCEPTION WHEN SQLSTATE '23505' THEN v_p4_rejected := true;
    END;
    IF NOT v_p4_rejected THEN RAISE EXCEPTION 'duplicate staff assignment did not raise SQLSTATE 23505'; END IF;
    INSERT INTO exam_task_staff(task_id, user_id, staff_role, assigned_by)
    VALUES (v_p4_task, v_p4_user, 'Invigilator', v_manager);
    SELECT count(*) INTO v_count FROM exam_task_staff
    WHERE task_id = v_p4_task AND user_id = v_p4_user AND staff_role IN ('Grader','Invigilator');
    IF v_count <> 2 THEN RAISE EXCEPTION 'staff role uniqueness assertion failed: %', v_count; END IF;

    -- Snapshot shape records a reproducible 32-byte seed and algorithm, never answer keys.
    v_p4_snapshot := jsonb_build_object(
        'seedHex', repeat('a', 64),
        'algorithmVersion', 'sha256-kuhn-v1',
        'paperVersionId', to_jsonb(v_paper_version::text),
        'sections', jsonb_build_array(jsonb_build_object(
            'sectionId', to_jsonb(v_section::text), 'title', '单项选择题',
            'questions', jsonb_build_array(jsonb_build_object(
                'paperQuestionId', to_jsonb(v_paper_question::text),
                'questionVersionId', to_jsonb(v_version::text),
                'sortOrder', 1, 'score', '5.00'
            ))
        ))
    );
    INSERT INTO exam_attempt(assignment_id, attempt_no, start_key, paper_snapshot_json,
                             started_at, deadline_at, max_score)
    VALUES (v_p4_assignment, 1, v_token || '-p4-auto-start', v_p4_snapshot, v_p4_now, v_p4_deadline, 5)
    RETURNING id INTO v_p4_auto_attempt;
    SELECT paper_snapshot_json INTO v_p4_stored_snapshot FROM exam_attempt WHERE id = v_p4_auto_attempt;
    IF v_p4_stored_snapshot IS DISTINCT FROM v_p4_snapshot
       OR jsonb_typeof(v_p4_stored_snapshot -> 'seedHex') <> 'string'
       OR v_p4_stored_snapshot ->> 'seedHex' !~ '^[0-9a-f]{64}$'
       OR v_p4_stored_snapshot ->> 'algorithmVersion' <> 'sha256-kuhn-v1'
       OR jsonb_typeof(v_p4_stored_snapshot -> 'paperVersionId') <> 'string'
       OR v_p4_stored_snapshot ->> 'paperVersionId' <> v_paper_version::text
       OR jsonb_typeof(v_p4_stored_snapshot -> 'sections') <> 'array'
       OR jsonb_array_length(v_p4_stored_snapshot -> 'sections') <> 1 THEN
        RAISE EXCEPTION 'paper snapshot reproducibility fields have an invalid shape';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM jsonb_array_elements(v_p4_stored_snapshot -> 'sections') AS s(section)
        WHERE jsonb_typeof(section -> 'sectionId') = 'string'
          AND section ->> 'sectionId' = v_section::text
          AND jsonb_typeof(section -> 'title') = 'string'
          AND section ->> 'title' = '单项选择题'
          AND jsonb_typeof(section -> 'questions') = 'array'
          AND jsonb_array_length(section -> 'questions') = 1
          AND EXISTS (
              SELECT 1 FROM jsonb_array_elements(section -> 'questions') AS q(question)
              WHERE jsonb_typeof(question -> 'paperQuestionId') = 'string'
                AND question ->> 'paperQuestionId' = v_paper_question::text
                AND jsonb_typeof(question -> 'questionVersionId') = 'string'
                AND question ->> 'questionVersionId' = v_version::text
                AND jsonb_typeof(question -> 'sortOrder') = 'number'
                AND question ->> 'sortOrder' = '1'
                AND jsonb_typeof(question -> 'score') = 'string'
                AND question ->> 'score' = '5.00'
          )
    ) THEN
        RAISE EXCEPTION 'paper snapshot section/question fields have an invalid shape';
    END IF;
    IF EXISTS (
        WITH RECURSIVE walk(node) AS (
            SELECT v_p4_stored_snapshot
            UNION ALL
            SELECT child.value
            FROM walk AS w
            CROSS JOIN LATERAL (
                SELECT value FROM jsonb_array_elements(
                    CASE WHEN jsonb_typeof(w.node) = 'array' THEN w.node ELSE '[]'::jsonb END)
                UNION ALL
                SELECT value FROM jsonb_each(
                    CASE WHEN jsonb_typeof(w.node) = 'object' THEN w.node ELSE '{}'::jsonb END)
            ) AS child
        ), object_keys AS (
            SELECT k.key
            FROM walk AS w
            CROSS JOIN LATERAL jsonb_each(
                CASE WHEN jsonb_typeof(w.node) = 'object' THEN w.node ELSE '{}'::jsonb END) AS k(key, value)
        )
        SELECT 1 FROM object_keys
        WHERE lower(key) IN ('answerjson','correctjson','iscorrect','analysis')
    ) THEN
        RAISE EXCEPTION 'paper snapshot contains an answer key or analysis';
    END IF;
    IF (SELECT COALESCE(sum((question ->> 'score')::numeric), 0)
        FROM jsonb_array_elements(v_p4_stored_snapshot -> 'sections') AS s(section)
        CROSS JOIN LATERAL jsonb_array_elements(section -> 'questions') AS q(question))
       <> (SELECT max_score FROM exam_attempt WHERE id = v_p4_auto_attempt) THEN
        RAISE EXCEPTION 'paper snapshot score total does not match max_score';
    END IF;

    -- AutoSubmitted is rejected at deadline+29s and accepted at the exact +30s boundary.
    v_p4_rejected := false;
    BEGIN
        UPDATE exam_attempt
        SET status = 'AutoSubmitted', submit_key = v_token || '-p4-auto-submit-early',
            submitted_at = v_p4_deadline + interval '29 seconds', is_late = true
        WHERE id = v_p4_auto_attempt;
        RAISE EXCEPTION 'AutoSubmitted before deadline+30s was accepted' USING ERRCODE = 'P0001';
    EXCEPTION WHEN SQLSTATE '23514' THEN v_p4_rejected := true;
    END;
    IF NOT v_p4_rejected THEN RAISE EXCEPTION 'early AutoSubmitted did not raise SQLSTATE 23514'; END IF;
    SELECT status, submitted_at INTO v_p4_row FROM exam_attempt WHERE id = v_p4_auto_attempt;
    IF v_p4_row.status <> 'InProgress' OR v_p4_row.submitted_at IS NOT NULL THEN
        RAISE EXCEPTION 'rejected early AutoSubmitted changed the attempt';
    END IF;
    UPDATE exam_attempt
    SET status = 'AutoSubmitted', submit_key = v_token || '-p4-auto-submit-boundary',
        submitted_at = v_p4_deadline + interval '30 seconds', is_late = true
    WHERE id = v_p4_auto_attempt;
    SELECT status, submitted_at, is_late INTO v_p4_row FROM exam_attempt WHERE id = v_p4_auto_attempt;
    IF v_p4_row.status <> 'AutoSubmitted' OR v_p4_row.submitted_at <> v_p4_deadline + interval '30 seconds'
       OR NOT v_p4_row.is_late THEN
        RAISE EXCEPTION 'AutoSubmitted grace boundary assertion failed';
    END IF;
    INSERT INTO attempt_event(attempt_id, actor_id, event_type, payload_json, event_key)
    VALUES (v_p4_auto_attempt, NULL, 'AutoSubmitted', '{"reason":"timeout"}'::jsonb,
            v_token || '-p4-auto-event');

    -- Administrative force submit stays Submitted inside grace; the event only proves persistence.
    INSERT INTO exam_attempt(assignment_id, attempt_no, start_key, paper_snapshot_json,
                             started_at, deadline_at, max_score)
    VALUES (v_p4_assignment, 2, v_token || '-p4-force-start', v_p4_snapshot, v_p4_now, v_p4_deadline, 5)
    RETURNING id INTO v_p4_force_attempt;
    UPDATE exam_attempt
    SET status = 'Submitted', submit_key = v_token || '-p4-force-submit',
        submitted_at = v_p4_deadline + interval '10 seconds', is_late = true
    WHERE id = v_p4_force_attempt;
    INSERT INTO attempt_event(attempt_id, actor_id, event_type, payload_json, event_key)
    VALUES (v_p4_force_attempt, v_manager, 'ForceSubmitted',
            jsonb_build_object('reason','Phase4 行政强制交卷冒烟'), v_token || '-p4-force-event');
    SELECT status, submitted_at, is_late INTO v_p4_row FROM exam_attempt WHERE id = v_p4_force_attempt;
    IF v_p4_row.status <> 'Submitted' OR v_p4_row.submitted_at <> v_p4_deadline + interval '10 seconds'
       OR NOT v_p4_row.is_late
       OR NOT EXISTS (SELECT 1 FROM attempt_event WHERE attempt_id = v_p4_force_attempt AND event_type = 'ForceSubmitted')
       OR EXISTS (SELECT 1 FROM attempt_event WHERE attempt_id = v_p4_force_attempt AND event_type = 'AutoSubmitted') THEN
        RAISE EXCEPTION 'administrative force-submit persistence assertion failed';
    END IF;

    -- VoidScore is a legal revocation scope; its zero grading facts survive Graded -> Voided.
    INSERT INTO exam_attempt(assignment_id, attempt_no, start_key, paper_snapshot_json,
                             started_at, deadline_at, max_score)
    VALUES (v_p4_assignment, 3, v_token || '-p4-void-start', v_p4_snapshot, v_p4_now, v_p4_deadline, 5)
    RETURNING id INTO v_p4_void_attempt;
    INSERT INTO attempt_answer(attempt_id, question_version_id, answer_json, score, max_score)
    VALUES (v_p4_void_attempt, v_version, '{"value":"A"}'::jsonb, 0, 5)
    RETURNING id INTO v_p4_void_answer;
    UPDATE exam_attempt
    SET status = 'Submitted', submit_key = v_token || '-p4-void-submit',
        submitted_at = v_p4_now + interval '5 minutes', is_late = false
    WHERE id = v_p4_void_attempt;
    UPDATE attempt_answer SET score = 0, grading_status = 'Graded' WHERE id = v_p4_void_answer;
    UPDATE exam_attempt SET status = 'Graded', auto_score = 0, score = 0,
        graded_at = v_p4_now + interval '6 minutes' WHERE id = v_p4_void_attempt;
    INSERT INTO revocation_record(target_type, target_id, scope, reason, impact_json,
                                  actor_id, actor_role, idempotency_key)
    VALUES ('ExamAssignment', v_p4_assignment, 'VoidScore', 'Phase4 VoidScore 策略冒烟',
            jsonb_build_object('policy','VoidScore','attemptId',to_jsonb(v_p4_void_attempt::text)),
            v_manager, 'User', v_token || '-p4-void-score-key')
    RETURNING id INTO v_p4_revocation;
    IF NOT EXISTS (
        SELECT 1 FROM revocation_record
        WHERE id = v_p4_revocation AND target_type = 'ExamAssignment' AND target_id = v_p4_assignment
          AND scope = 'VoidScore' AND actor_id = v_manager AND actor_role = 'User'
          AND jsonb_typeof(impact_json) = 'object'
    ) THEN
        RAISE EXCEPTION 'VoidScore revocation_record did not round-trip';
    END IF;
    SELECT jsonb_build_object('answerJson', aa.answer_json, 'answerScore', aa.score, 'autoScore', a.auto_score)
    INTO v_p4_before
    FROM exam_attempt a JOIN attempt_answer aa ON aa.attempt_id = a.id
    WHERE a.id = v_p4_void_attempt AND aa.id = v_p4_void_answer;
    INSERT INTO revocation_record(target_type, target_id, scope, reason, actor_id, actor_role, idempotency_key)
    VALUES ('ExamAttempt', v_p4_void_attempt, 'VoidAttempt', 'Phase4 Graded 答卷作废冒烟',
            v_manager, 'User', v_token || '-p4-void-attempt-key');
    UPDATE exam_attempt SET status = 'Voided', score = 0,
        voided_at = v_p4_now + interval '7 minutes', void_reason = 'Phase4 Graded 答卷作废冒烟'
    WHERE id = v_p4_void_attempt;
    SELECT jsonb_build_object('answerJson', aa.answer_json, 'answerScore', aa.score, 'autoScore', a.auto_score)
    INTO v_p4_after
    FROM exam_attempt a JOIN attempt_answer aa ON aa.attempt_id = a.id
    WHERE a.id = v_p4_void_attempt AND aa.id = v_p4_void_answer;
    SELECT status, score INTO v_p4_row FROM exam_attempt WHERE id = v_p4_void_attempt;
    IF v_p4_row.status <> 'Voided' OR v_p4_row.score <> 0 OR v_p4_before IS DISTINCT FROM v_p4_after
       OR (SELECT count(*) FROM attempt_answer WHERE attempt_id = v_p4_void_attempt) <> 1
       OR (SELECT score FROM attempt_answer WHERE id = v_p4_void_answer) <> 0
       OR (SELECT max_score FROM attempt_answer WHERE id = v_p4_void_answer) <> 5 THEN
        RAISE EXCEPTION 'Graded -> Voided did not preserve zero answer/auto_score facts and zero final score';
    END IF;
    v_p4_rejected := false;
    BEGIN
        UPDATE exam_attempt SET status = 'Graded', score = 1 WHERE id = v_p4_void_attempt;
        RAISE EXCEPTION 'Voided attempt was modified' USING ERRCODE = 'P0001';
    EXCEPTION WHEN SQLSTATE '23514' THEN v_p4_rejected := true;
    END;
    IF NOT v_p4_rejected THEN RAISE EXCEPTION 'Voided attempt update did not raise SQLSTATE 23514'; END IF;
    v_p4_rejected := false;
    BEGIN
        UPDATE attempt_answer SET answer_json = '{"value":"B"}'::jsonb WHERE id = v_p4_void_answer;
        RAISE EXCEPTION 'Voided attempt answer was modified' USING ERRCODE = 'P0001';
    EXCEPTION WHEN SQLSTATE '23514' THEN v_p4_rejected := true;
    END;
    IF NOT v_p4_rejected THEN RAISE EXCEPTION 'Voided answer update did not raise SQLSTATE 23514'; END IF;

    -- Outbox retries persist exponential backoff and become Dead on the fifth failure.
    INSERT INTO outbox_message(event_key, type, aggregate_type, aggregate_id, payload_json,
                               status, retry_count, next_attempt_at)
    VALUES (v_token || '-p4-outbox-retry', 'exam_attempt.submitted', 'ExamAttempt', v_p4_force_attempt,
            jsonb_build_object('attemptId',to_jsonb(v_p4_force_attempt::text)), 'Pending', 0, v_p4_now)
    RETURNING id INTO v_p4_outbox_retry;
    v_p4_next := v_p4_now;
    FOR v_p4_i IN 1..5 LOOP
        UPDATE outbox_message SET status = 'Processing', locked_at = v_p4_next, row_version = row_version + 1
        WHERE id = v_p4_outbox_retry;
        SELECT status, retry_count, locked_at INTO v_p4_row FROM outbox_message WHERE id = v_p4_outbox_retry;
        IF v_p4_row.status <> 'Processing' OR v_p4_row.retry_count <> v_p4_i - 1
           OR v_p4_row.locked_at <> v_p4_next THEN
            RAISE EXCEPTION 'outbox Processing lease assertion failed at retry %', v_p4_i;
        END IF;
        UPDATE outbox_message
        SET status = CASE WHEN v_p4_i = 5 THEN 'Dead' ELSE 'Pending' END,
            retry_count = v_p4_i,
            next_attempt_at = CASE WHEN v_p4_i < 5
                THEN v_p4_next + make_interval(secs => 15 * (1 << (v_p4_i - 1)))
                ELSE v_p4_next END,
            locked_at = NULL,
            last_error = 'smoke failure ' || v_p4_i,
            row_version = row_version + 1
        WHERE id = v_p4_outbox_retry;
        SELECT status, retry_count, next_attempt_at, locked_at, last_error, sent_at
        INTO v_p4_row FROM outbox_message WHERE id = v_p4_outbox_retry;
        IF v_p4_row.retry_count <> v_p4_i OR v_p4_row.locked_at IS NOT NULL
           OR v_p4_row.last_error <> ('smoke failure ' || v_p4_i) OR v_p4_row.sent_at IS NOT NULL THEN
            RAISE EXCEPTION 'outbox retry metadata assertion failed at retry %', v_p4_i;
        END IF;
        IF v_p4_i < 5 THEN
            v_p4_delay := 15 * (1 << (v_p4_i - 1));
            IF v_p4_row.status <> 'Pending'
               OR v_p4_row.next_attempt_at <> v_p4_next + make_interval(secs => v_p4_delay) THEN
                RAISE EXCEPTION 'outbox exponential backoff assertion failed at retry %', v_p4_i;
            END IF;
            v_p4_next := v_p4_row.next_attempt_at;
        ELSIF v_p4_row.status <> 'Dead' OR v_p4_row.next_attempt_at <> v_p4_next THEN
            RAISE EXCEPTION 'outbox fifth-failure Dead shape assertion failed';
        END IF;
    END LOOP;
    v_p4_rejected := false;
    BEGIN
        UPDATE outbox_message SET status = 'Pending' WHERE id = v_p4_outbox_retry;
        RAISE EXCEPTION 'Dead outbox message was reopened' USING ERRCODE = 'P0001';
    EXCEPTION WHEN SQLSTATE '23514' THEN v_p4_rejected := true;
    END;
    IF NOT v_p4_rejected THEN RAISE EXCEPTION 'Dead outbox reopen did not raise SQLSTATE 23514'; END IF;

    INSERT INTO outbox_message(event_key, type, aggregate_type, aggregate_id, status, next_attempt_at)
    VALUES (v_token || '-p4-outbox-sent', 'exam_attempt.submitted', 'ExamAttempt', v_p4_force_attempt,
            'Pending', v_p4_now)
    RETURNING id INTO v_p4_outbox_sent;
    UPDATE outbox_message SET status = 'Sent', sent_at = v_p4_now, locked_at = NULL
    WHERE id = v_p4_outbox_sent;
    v_p4_rejected := false;
    BEGIN
        UPDATE outbox_message SET status = 'Pending' WHERE id = v_p4_outbox_sent;
        RAISE EXCEPTION 'Sent outbox message was reopened' USING ERRCODE = 'P0001';
    EXCEPTION WHEN SQLSTATE '23514' THEN v_p4_rejected := true;
    END;
    IF NOT v_p4_rejected THEN RAISE EXCEPTION 'Sent outbox reopen did not raise SQLSTATE 23514'; END IF;

    -- An expired Processing lease remains retryable and clears only its lock on recovery.
    INSERT INTO outbox_message(event_key, type, aggregate_type, aggregate_id, status,
                               retry_count, next_attempt_at)
    VALUES (v_token || '-p4-outbox-stale', 'exam_attempt.submitted', 'ExamAttempt', v_p4_force_attempt,
            'Pending', 1, v_p4_now + interval '1 hour')
    RETURNING id INTO v_p4_outbox_stale;
    UPDATE outbox_message SET status = 'Processing', locked_at = v_p4_now - interval '3 minutes',
        last_error = 'prior smoke failure', row_version = row_version + 1
    WHERE id = v_p4_outbox_stale;
    IF NOT EXISTS (
        SELECT 1 FROM outbox_message
        WHERE id = v_p4_outbox_stale AND status = 'Processing'
          AND locked_at < v_p4_now - interval '2 minutes' AND sent_at IS NULL
    ) THEN
        RAISE EXCEPTION 'expired Processing lease was not selectable for recovery';
    END IF;
    UPDATE outbox_message SET status = 'Pending', locked_at = NULL,
        next_attempt_at = v_p4_now + interval '15 seconds', row_version = row_version + 1
    WHERE id = v_p4_outbox_stale;
    SELECT status, retry_count, next_attempt_at, locked_at, last_error, sent_at
    INTO v_p4_row FROM outbox_message WHERE id = v_p4_outbox_stale;
    IF v_p4_row.status <> 'Pending' OR v_p4_row.retry_count <> 1 OR v_p4_row.locked_at IS NOT NULL
       OR v_p4_row.next_attempt_at <> v_p4_now + interval '15 seconds'
       OR v_p4_row.last_error <> 'prior smoke failure' OR v_p4_row.sent_at IS NOT NULL THEN
        RAISE EXCEPTION 'expired Processing lease recovery fields are incorrect';
    END IF;

    -- Phase 5: immutable publication snapshots, NULL final scores, orthogonal withdrawal and export trace.
    INSERT INTO exam_task(department_id, code, title, exam_date, available_from, available_to, created_by,
                          score_policy, pass_score, result_release_policy)
    VALUES (v_root, 'SMOKE-P5-' || v_token, 'Phase5 冒烟考试', DATE '2030-01-01',
            timestamp '2030-01-01 09:00:00', timestamp '2030-01-01 10:00:00', v_manager,
            'Highest', 4, 'Manual') RETURNING id INTO v_p5_task;
    UPDATE exam_task SET paper_version_id = v_paper_version, status = 'Published', published_at = v_p4_now
    WHERE id = v_p5_task;
    INSERT INTO exam_assignment(task_id, user_id, department_id_snapshot, status, available_from, available_to)
    VALUES (v_p5_task, v_user, v_child, 'Pending',
            timestamp '2030-01-01 09:00:00', timestamp '2030-01-01 10:00:00') RETURNING id INTO v_p5_assignment;
    UPDATE exam_assignment SET status = 'Available' WHERE id = v_p5_assignment;
    INSERT INTO exam_attempt(assignment_id, attempt_no, start_key, paper_snapshot_json,
                             started_at, deadline_at, max_score)
    VALUES (v_p5_assignment, 1, v_token || '-p5-start', v_p4_snapshot, v_p4_now, v_p4_deadline, 5)
    RETURNING id INTO v_p5_attempt;
    INSERT INTO attempt_answer(attempt_id, question_version_id, answer_json, max_score)
    VALUES (v_p5_attempt, v_version, '{"value":"A"}'::jsonb, 5);
    UPDATE exam_attempt SET status = 'Submitted', submit_key = v_token || '-p5-submit',
        submitted_at = v_p4_now + interval '5 minutes', is_late = false WHERE id = v_p5_attempt;
    UPDATE attempt_answer SET score = 5, grading_status = 'Graded' WHERE attempt_id = v_p5_attempt;
    UPDATE exam_attempt SET status = 'Graded', auto_score = 5, score = 5,
        graded_at = v_p4_now + interval '6 minutes' WHERE id = v_p5_attempt;
    INSERT INTO result_release(task_id, release_no, score_policy, pass_score, result_release_policy,
                               summary_json, payload_sha256, published_by, published_at, idempotency_key)
    VALUES (v_p5_task, 1, 'Highest', 4, 'Manual',
            jsonb_build_object('assignmentCount',1,'scoredCount',1), repeat('a',64), v_manager, v_p4_now,
            v_token || '-p5-release-key') RETURNING id INTO v_p5_release;
    INSERT INTO result_release_item(release_id, task_id, assignment_id, user_id, department_id_snapshot,
                                    attempt_id, final_score, max_score, passed, source_json)
    VALUES (v_p5_release, v_p5_task, v_p5_assignment, v_user, v_child,
            v_p5_attempt, 5, 5, true, jsonb_build_object('attemptNo',1,'autoScore','5.00'))
    RETURNING id INTO v_p5_item;
    INSERT INTO report_export(task_id, report_type, format, file_key, file_name, row_count,
                              filter_json, created_by, created_at, expires_at)
    VALUES (v_p5_task, 'Summary', 'CSV', '2026/11/' || v_token || '.csv', '成绩汇总.csv', 1,
            jsonb_build_object('departmentId',to_jsonb(v_child::text)), v_manager, v_p4_now, v_p4_now + interval '24 hours')
    RETURNING id INTO v_p5_export;

    UPDATE exam_task SET result_status = 'Published', result_published_at = v_p4_now, result_release_id = v_p5_release
    WHERE id = v_p5_task;
    IF NOT EXISTS (SELECT 1 FROM result_release_item WHERE id = v_p5_item AND final_score = 5 AND passed) THEN
        RAISE EXCEPTION 'Phase5 release item did not round-trip';
    END IF;
    BEGIN
        INSERT INTO result_release_item(release_id, task_id, assignment_id, user_id, department_id_snapshot,
                                        attempt_id, final_score, max_score, passed)
        VALUES (v_p5_release, v_task, v_assignment, v_user, v_child, v_attempt, 4, 5, false);
        RAISE EXCEPTION 'release item crossed its release task';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        UPDATE exam_task SET result_release_id = v_p5_release WHERE id = v_task;
        RAISE EXCEPTION 'task pointer crossed its release task';
    EXCEPTION WHEN check_violation OR foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO score_adjustment(attempt_id, old_score, new_score, reason, adjusted_by, idempotency_key)
        VALUES (v_p5_attempt, 5, 4, 'published score adjustment must fail', v_manager, v_token || '-p5-published-adjust');
        RAISE EXCEPTION 'score adjustment while Published was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE result_release SET summary_json = '{"tampered":true}'::jsonb WHERE id = v_p5_release;
        RAISE EXCEPTION 'result_release update was not rejected';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;
    BEGIN
        UPDATE result_release_item SET final_score = 4 WHERE id = v_p5_item;
        RAISE EXCEPTION 'result_release_item update was not rejected';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;
    BEGIN
        DELETE FROM report_export WHERE id = v_p5_export;
        RAISE EXCEPTION 'report_export delete was not rejected';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;
    BEGIN
        TRUNCATE TABLE result_release_item;
        RAISE EXCEPTION 'result_release_item truncate was not rejected';
    EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
    END;
    BEGIN
        UPDATE exam_task SET result_status = 'Unpublished' WHERE id = v_p5_task;
        RAISE EXCEPTION 'Published result transitioned directly to Unpublished';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    UPDATE exam_task SET result_status = 'Withdrawn', result_withdrawn_at = v_p4_now + interval '7 minutes',
        result_withdraw_reason = 'Phase5 score correction review' WHERE id = v_p5_task;
    IF (SELECT final_score FROM result_release_item WHERE id = v_p5_item) <> 5 THEN
        RAISE EXCEPTION 'withdrawing results rewrote the historical release snapshot';
    END IF;
    INSERT INTO result_release(task_id, release_no, score_policy, pass_score, result_release_policy,
                               summary_json, payload_sha256, published_by, published_at, idempotency_key)
    VALUES (v_p5_task, 2, 'Highest', 4, 'Manual', '{}'::jsonb, repeat('b',64), v_manager,
            v_p4_now + interval '8 minutes', v_token || '-p5-release-key-2') RETURNING id INTO v_p5_release_withdrawn;
    UPDATE exam_task SET result_status = 'Published', result_published_at = v_p4_now + interval '8 minutes',
        result_release_id = v_p5_release_withdrawn, result_withdrawn_at = NULL, result_withdraw_reason = NULL
    WHERE id = v_p5_task;
    IF (SELECT count(*) FROM result_release WHERE task_id = v_p5_task) <> 2 THEN
        RAISE EXCEPTION 'republishing did not append a new release';
    END IF;
    BEGIN
        INSERT INTO result_release_item(release_id, task_id, assignment_id, user_id, department_id_snapshot,
                                        attempt_id, final_score, max_score, passed)
        VALUES (v_p5_release_withdrawn, v_p5_task, v_p5_assignment, v_manager, v_child, v_p5_attempt, 4, 5, false);
        RAISE EXCEPTION 'result_release_item accepted a mismatched assignment user' USING ERRCODE = 'P0001';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- Import preview/confirmation counts and failed rows never reference a question.
    INSERT INTO import_batch(bank_id, file_name, file_key, file_hash, mode, status, total_rows,
                             valid_rows, invalid_rows, warning_rows, created_by)
    VALUES (v_bank, 'smoke.xlsx', 'smoke/smoke.xlsx', repeat('b', 64), 'SkipInvalid', 'Previewed',
            2, 1, 1, 1, v_manager) RETURNING id INTO v_import_batch;
    BEGIN
        INSERT INTO import_batch(bank_id, file_name, file_key, file_hash, created_by)
        VALUES (v_bank, 'smoke-copy.xlsx', 'smoke/copy.xlsx', repeat('b', 64), v_manager);
        RAISE EXCEPTION 'duplicate import file hash was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    INSERT INTO import_row(batch_id, row_no, external_code, status, raw_json, warning_json)
    VALUES (v_import_batch, 1, 'SMOKE-IMPORT-1', 'Warning', '{"stem":"预览题干"}'::jsonb, '["题干重复"]'::jsonb);
    INSERT INTO import_row(batch_id, row_no, external_code, status, raw_json, error_json)
    VALUES (v_import_batch, 2, 'SMOKE-IMPORT-2', 'Invalid', '{}'::jsonb, '["题型不受支持"]'::jsonb);
    BEGIN
        UPDATE import_row SET question_id = v_question WHERE batch_id = v_import_batch AND row_no = 2;
        RAISE EXCEPTION 'Invalid import row referenced a question';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE import_batch SET status = 'Completed', imported_rows = 1, finished_at = now(), confirmed_at = now()
        WHERE id = v_import_batch;
        RAISE EXCEPTION 'unbalanced terminal import counts were accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    UPDATE import_batch SET status = 'Importing', confirmed_at = now() WHERE id = v_import_batch;

    INSERT INTO question(bank_id, external_code, type) VALUES (v_bank, 'SMOKE-IMPORT-1', 'TrueFalse') RETURNING id INTO v_import_question;
    INSERT INTO question_version(question_id, version_no, stem, answer_json, default_score, source, import_batch_id, import_row_id)
    SELECT v_import_question, 1, '导入判断', '{"value":true}'::jsonb, 2, 'Import', v_import_batch, id
    FROM import_row WHERE batch_id = v_import_batch AND row_no = 1 RETURNING id INTO v_import_version;
    INSERT INTO question_option(question_version_id, label, content, is_correct, sort_order)
    VALUES (v_import_version, 'A', '正确', true, 1), (v_import_version, 'B', '错误', false, 2);
    UPDATE import_row SET status = 'Imported', question_id = v_import_question, raw_json = '{}'::jsonb,
        answer_summary = 'TrueFalse:true' WHERE batch_id = v_import_batch AND row_no = 1;
    UPDATE import_batch SET status = 'PartiallyFailed', imported_rows = 1, failed_rows = 1, finished_at = now()
    WHERE id = v_import_batch;
    BEGIN
        UPDATE import_batch SET failed_rows = 0 WHERE id = v_import_batch;
        RAISE EXCEPTION 'terminal import batch was rewritten';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Time三型, defaults, singleton metadata and ACL are checked against the real application role.
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='department'
                   AND column_name='created_at' AND data_type='timestamp with time zone') THEN
        RAISE EXCEPTION 'department.created_at must be timestamptz';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='exam_task'
                   AND column_name='available_from' AND data_type='timestamp without time zone') THEN
        RAISE EXCEPTION 'exam_task.available_from must be timestamp';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='exam_task'
                   AND column_name='exam_date' AND data_type='date') THEN
        RAISE EXCEPTION 'exam_task.exam_date must be date';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='exam_task'
                   AND column_name='retake_deadline_at' AND data_type='timestamp with time zone') THEN
        RAISE EXCEPTION 'exam_task.retake_deadline_at must be timestamptz';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='exam_attempt'
                   AND column_name='score' AND data_type='numeric' AND numeric_precision=6 AND numeric_scale=2) THEN
        RAISE EXCEPTION 'exam_attempt.score must be numeric(6,2)';
    END IF;
    IF (SELECT row_version FROM exam_task WHERE id = v_task) <> 1 THEN
        RAISE EXCEPTION 'row_version default must be 1';
    END IF;
    IF (SELECT count(*) FROM schema_meta) <> 1 OR NOT EXISTS (SELECT 1 FROM schema_meta WHERE id=1) THEN
        RAISE EXCEPTION 'schema_meta must contain singleton id=1';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM schema_meta WHERE app_compat_min IS NOT NULL AND app_compat_max IS NOT NULL AND released_at IS NOT NULL) THEN
        RAISE EXCEPTION 'schema_meta compatibility declaration is incomplete';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='examops_app') THEN
        RAISE EXCEPTION 'security.sql must be applied before smoke_test.sql';
    END IF;
    IF has_table_privilege('examops_app','audit_event','UPDATE')
       OR has_table_privilege('examops_app','audit_event','DELETE')
       OR has_table_privilege('examops_app','audit_event','TRUNCATE')
       OR NOT has_table_privilege('examops_app','audit_event','INSERT') THEN
        RAISE EXCEPTION 'examops_app audit_event ACL must be SELECT/INSERT only';
    END IF;
    IF has_table_privilege('examops_app','attempt_event','UPDATE')
       OR has_table_privilege('examops_app','attempt_event','DELETE')
       OR has_table_privilege('examops_app','attempt_event','TRUNCATE')
       OR NOT has_table_privilege('examops_app','attempt_event','INSERT') THEN
        RAISE EXCEPTION 'examops_app attempt_event ACL must be SELECT/INSERT only';
    END IF;
    IF has_table_privilege('examops_app','score_adjustment','UPDATE')
       OR has_table_privilege('examops_app','retake_grant','DELETE')
       OR has_table_privilege('examops_app','revocation_record','TRUNCATE')
       OR has_table_privilege('examops_app','result_release','UPDATE')
       OR has_table_privilege('examops_app','result_release_item','DELETE')
       OR has_table_privilege('examops_app','report_export','UPDATE')
       OR NOT has_table_privilege('examops_app','result_release','INSERT')
       OR NOT has_table_privilege('examops_app','result_release_item','INSERT')
       OR NOT has_table_privilege('examops_app','report_export','INSERT') THEN
        RAISE EXCEPTION 'examops_app can mutate Phase5 append-only facts';
    END IF;
    IF has_table_privilege('examops_app','schema_meta','INSERT')
       OR has_table_privilege('examops_app','permission','INSERT')
       OR has_table_privilege('examops_app','role','UPDATE')
       OR has_table_privilege('examops_app','role_permission','DELETE')
       OR NOT has_table_privilege('examops_app','outbox_message','UPDATE') THEN
        RAISE EXCEPTION 'examops_app metadata/role/outbox ACL is incorrect';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname='public' AND indexname='ux_exam_assignment_active') THEN
        RAISE EXCEPTION 'missing active assignment unique index';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname='public' AND indexname='ix_audit_entity_created') THEN
        RAISE EXCEPTION 'missing audit query index';
    END IF;
END $smoke$;

ROLLBACK;

\echo 'ExamOps smoke assertions passed; all test data rolled back.'
