-- ExamOps seed.sql
-- Idempotent local bootstrap data. Never contains a working password hash.
-- Set the initial admin password with `EXAMOPS_ADMIN_PASSWORD=... dotnet run -- --init-admin`.
-- Permission grants below are the exact nine-role matrix in docs/permission-matrix.md.

INSERT INTO role(code, name, is_system, is_active, scope_type) VALUES
    ('PlatformAdmin', '平台管理员', true, true, 'All'),
    ('OrgAdmin', '组织管理员', true, true, 'All'),
    ('DeptAdmin', '部门管理员', true, true, 'DepartmentSubtree'),
    ('ExamManager', '考务管理员', true, true, 'DepartmentSubtree'),
    ('QuestionManager', '题库管理员', true, true, 'DepartmentSubtree'),
    ('Grader', '阅卷复核员', true, true, 'Assigned'),
    ('Invigilator', '监考员', true, true, 'DepartmentSubtree'),
    ('Auditor', '审计员', true, true, 'All'),
    ('Candidate', '考生', true, true, 'Self')
ON CONFLICT (code) DO NOTHING;

-- Merge legacy system roles without losing user authorizations, then remove the obsolete role codes.
DO $legacy_roles$
DECLARE
    v_old role%ROWTYPE;
    v_new_id bigint;
BEGIN
    FOR v_old IN SELECT * FROM role WHERE code IN ('DepartmentAdmin','Proctor','ReadOnlyAuditor') LOOP
        IF v_old.code = 'DepartmentAdmin' THEN
            SELECT id INTO v_new_id FROM role WHERE code = 'DeptAdmin';
        ELSIF v_old.code = 'Proctor' THEN
            SELECT id INTO v_new_id FROM role WHERE code = 'Invigilator';
        ELSE
            SELECT id INTO v_new_id FROM role WHERE code = 'Auditor';
        END IF;

        UPDATE user_role old_ur
        SET role_id = v_new_id, department_id = CASE
            WHEN target_role.scope_type IN ('Department','DepartmentSubtree') THEN old_ur.department_id
            ELSE NULL END
        FROM user_role candidate_ur
        JOIN role target_role ON target_role.id = v_new_id
        WHERE old_ur.id = candidate_ur.id
          AND candidate_ur.role_id = v_old.id
          AND NOT EXISTS (
              SELECT 1 FROM user_role existing
              WHERE existing.user_id = candidate_ur.user_id
                AND existing.role_id = v_new_id
                AND existing.department_id IS NOT DISTINCT FROM CASE
                    WHEN target_role.scope_type IN ('Department','DepartmentSubtree') THEN candidate_ur.department_id
                    ELSE NULL END
          );

        DELETE FROM user_role WHERE role_id = v_old.id;
        DELETE FROM role_permission WHERE role_id = v_old.id;
        DELETE FROM role WHERE id = v_old.id;
    END LOOP;
END $legacy_roles$;

-- All/Assigned/Self authorizations have no department anchor; department-scoped anchors are never guessed.
UPDATE user_role ur SET department_id = NULL
FROM role r
WHERE r.id = ur.role_id AND r.scope_type IN ('All','Assigned','Self') AND ur.department_id IS NOT NULL;
DO $seed_scope_assert$
BEGIN
    IF EXISTS (
        SELECT 1 FROM user_role ur JOIN role r ON r.id = ur.role_id
        WHERE r.scope_type IN ('Department','DepartmentSubtree') AND ur.department_id IS NULL
    ) THEN
        RAISE EXCEPTION 'department-scoped user_role rows require an explicit department_id';
    END IF;
END $seed_scope_assert$;

UPDATE role SET name = v.name, is_system = true, is_active = true
FROM (VALUES
    ('PlatformAdmin', '平台管理员'),
    ('OrgAdmin', '组织管理员'),
    ('DeptAdmin', '部门管理员'),
    ('ExamManager', '考务管理员'),
    ('QuestionManager', '题库管理员'),
    ('Grader', '阅卷复核员'),
    ('Invigilator', '监考员'),
    ('Auditor', '审计员'),
    ('Candidate', '考生')
) AS v(code, name)
WHERE role.code = v.code;

INSERT INTO permission(code, name, module) VALUES
    ('org.department.read', '查看部门', 'org'),
    ('org.department.create', '创建部门', 'org'),
    ('org.department.update', '修改或移动部门', 'org'),
    ('org.user.read', '查看用户', 'org'),
    ('org.user.create', '创建用户', 'org'),
    ('org.user.update', '修改用户', 'org'),
    ('org.user.disable', '停用用户', 'org'),
    ('org.role.assign', '授予角色与部门范围', 'org'),
    ('question.read', '查看题库与题目', 'question'),
    ('question.create', '创建题目', 'question'),
    ('question.update', '修改题目草稿', 'question'),
    ('question.publish', '发布题目版本', 'question'),
    ('question.import', '导入题目', 'question'),
    ('paper.read', '查看试卷', 'paper'),
    ('paper.create', '创建试卷', 'paper'),
    ('paper.update', '修改试卷草稿', 'paper'),
    ('paper.publish', '发布试卷版本', 'paper'),
    ('paper.archive', '归档试卷', 'paper'),
    ('exam.task.read', '查看考试任务', 'exam'),
    ('exam.task.create', '创建考试任务', 'exam'),
    ('exam.task.update', '修改或归档考试任务', 'exam'),
    ('exam.task.publish', '发布或重开考试任务', 'exam'),
    ('exam.task.close', '关闭考试任务', 'exam'),
    ('exam.task.revoke', '撤回考试任务', 'exam'),
    ('exam.assignment.read', '查看考试安排', 'exam'),
    ('exam.assignment.create', '下发考试安排', 'exam'),
    ('exam.assignment.revoke', '撤回安排或下发批次', 'exam'),
    ('exam.assignment.grant_retake', '增加重考次数', 'exam'),
    ('exam.attempt.read', '查看答卷', 'exam'),
    ('exam.attempt.grade', '自动判分与复核诊断', 'exam'),
    ('exam.attempt.adjust_score', '调整答卷总分', 'exam'),
    ('exam.attempt.void', '作废答卷', 'exam'),
    ('exam.result.publish', '发布或撤回成绩', 'exam'),
    ('audit.read', '查看审计日志', 'audit'),
    ('audit.export', '导出审计日志', 'audit'),
    ('report.read', '查看报表', 'report'),
    ('report.export', '导出报表', 'report')
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, module = EXCLUDED.module;

-- Remove deprecated permission codes from every role before rebuilding the canonical system-role grants.
DELETE FROM role_permission rp USING permission p
WHERE p.id = rp.permission_id AND p.code IN (
    'platform.settings.manage','org.department.view','org.department.manage','org.user.view','org.user.manage',
    'org.role.view','org.role.manage','question.view','question.manage','question.review','paper.view','paper.manage',
    'exam.view','exam.manage','assignment.manage','retake.manage','exam.proctor','result.view','result.adjust',
    'result.void','audit.view','notification.view','exam.self.view','exam.self.answer','result.self.view','account.password'
);
DELETE FROM permission WHERE code IN (
    'platform.settings.manage','org.department.view','org.department.manage','org.user.view','org.user.manage',
    'org.role.view','org.role.manage','question.view','question.manage','question.review','paper.view','paper.manage',
    'exam.view','exam.manage','assignment.manage','retake.manage','exam.proctor','result.view','result.adjust',
    'result.void','audit.view','notification.view','exam.self.view','exam.self.answer','result.self.view','account.password'
);

DELETE FROM role_permission WHERE role_id IN (SELECT id FROM role WHERE is_system);

WITH grants(role_code, permission_codes) AS (
    VALUES
    ('PlatformAdmin', ARRAY[(SELECT array_agg(code ORDER BY code) FROM permission)]::text[]),
    ('OrgAdmin', ARRAY[
        'org.department.read','org.department.create','org.department.update','org.user.read','org.user.create',
        'org.user.update','org.user.disable','org.role.assign','question.read','paper.read',
        'exam.task.read','exam.task.create','exam.task.update','exam.task.publish','exam.task.close','exam.task.revoke',
        'exam.assignment.read','exam.assignment.create','exam.assignment.revoke','exam.assignment.grant_retake',
        'exam.attempt.read','exam.attempt.void','exam.result.publish','report.read','report.export'
    ]::text[]),
    ('DeptAdmin', ARRAY[
        'org.department.read','org.user.read','org.user.create','org.user.update','org.user.disable','question.read','paper.read',
        'exam.task.read','exam.task.create','exam.task.update','exam.task.publish','exam.task.close','exam.task.revoke',
        'exam.assignment.read','exam.assignment.create','exam.assignment.revoke','exam.assignment.grant_retake',
        'exam.attempt.read','exam.attempt.void','exam.result.publish','report.read','report.export'
    ]::text[]),
    ('ExamManager', ARRAY[
        'org.department.read','org.user.read','question.read','paper.read',
        'exam.task.read','exam.task.create','exam.task.update','exam.task.publish','exam.task.close','exam.task.revoke',
        'exam.assignment.read','exam.assignment.create','exam.assignment.revoke','exam.assignment.grant_retake',
        'exam.attempt.read','exam.attempt.void','exam.result.publish','report.read','report.export'
    ]::text[]),
    ('QuestionManager', ARRAY[
        'org.department.read','question.read','question.create','question.update','question.publish','question.import',
        'paper.read','paper.create','paper.update','paper.publish','paper.archive','exam.task.read','report.read'
    ]::text[]),
    ('Grader', ARRAY[
        'question.read','paper.read','exam.task.read','exam.assignment.read','exam.attempt.read',
        'exam.attempt.grade','exam.attempt.adjust_score','exam.attempt.void','report.read'
    ]::text[]),
    ('Invigilator', ARRAY[
        'org.department.read','org.user.read','exam.task.read','exam.assignment.read','exam.attempt.read'
    ]::text[]),
    ('Auditor', ARRAY['audit.read','audit.export','report.read']::text[]),
    ('Candidate', ARRAY['exam.task.read','exam.assignment.read','exam.attempt.read']::text[])
)
INSERT INTO role_permission(role_id, permission_id)
SELECT r.id, p.id
FROM grants g
JOIN role r ON r.code = g.role_code
CROSS JOIN LATERAL unnest(g.permission_codes) AS granted(code)
JOIN permission p ON p.code = granted.code;

DO $seed_assert$
BEGIN
    IF (SELECT count(*) FROM role WHERE is_system) <> 9 THEN
        RAISE EXCEPTION 'seed requires exactly nine system roles';
    END IF;
    IF (SELECT count(*) FROM permission) <> 37 THEN
        RAISE EXCEPTION 'seed requires exactly the 37 canonical permission codes';
    END IF;
END $seed_assert$;

INSERT INTO department(parent_id, name, path, path_segment, sort_order)
VALUES (NULL, '示例组织', '001.', '001', 0)
ON CONFLICT (path) DO NOTHING;

INSERT INTO department(parent_id, name, path, path_segment, sort_order)
SELECT p.id, v.name, v.path, v.path_segment, v.sort_order
FROM (VALUES
    ('安全生产部', '001.001.', '001', 1),
    ('设备管理部', '001.002.', '002', 2),
    ('综合管理部', '001.003.', '003', 3)
) AS v(name, path, path_segment, sort_order)
JOIN department p ON p.path = '001.'
ON CONFLICT (path) DO NOTHING;

INSERT INTO department(parent_id, name, path, path_segment, sort_order)
SELECT p.id, v.name, v.path, v.path_segment, v.sort_order
FROM (VALUES
    ('安全监察组', '001.001.001.', '001', 1),
    ('培训管理组', '001.001.002.', '002', 2)
) AS v(name, path, path_segment, sort_order)
JOIN department p ON p.path = '001.001.'
ON CONFLICT (path) DO NOTHING;

INSERT INTO app_user(username, display_name, email, password_hash, must_change_password, status, primary_department_id)
SELECT 'admin', '平台管理员', NULL, '', true, 'Active', id
FROM department WHERE path = '001.'
ON CONFLICT (username) DO NOTHING;

INSERT INTO user_department(user_id, department_id, is_primary)
SELECT u.id, u.primary_department_id, true
FROM app_user u
WHERE u.username = 'admin' AND u.primary_department_id IS NOT NULL
ON CONFLICT (user_id, department_id) DO UPDATE SET is_primary = true;

-- PlatformAdmin has All scope, so its department anchor must be NULL.
INSERT INTO user_role(user_id, role_id, department_id)
SELECT u.id, r.id, NULL
FROM app_user u JOIN role r ON r.code = 'PlatformAdmin'
WHERE u.username = 'admin'
ON CONFLICT (user_id, role_id, department_id) DO NOTHING;

INSERT INTO audit_event(actor_id, actor_role, action, entity_type, entity_id, after_json, result_ref_json, event_key)
SELECT u.id, 'PlatformAdmin', 'system.seed', 'AppUser', u.id,
       jsonb_build_object('bootstrap', true), jsonb_build_object('entityType', 'AppUser', 'entityId', u.id::text),
       'seed:AppUser:' || u.id::text
FROM app_user u
WHERE u.username = 'admin'
ON CONFLICT (event_key) WHERE event_key IS NOT NULL DO NOTHING;
