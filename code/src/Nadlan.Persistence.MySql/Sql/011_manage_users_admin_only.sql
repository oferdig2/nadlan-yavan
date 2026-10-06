-- 011: "Manage users" is Admin-only (2026-10-06). The app no longer honours it on other roles and the role editor no
-- longer offers it; this removes it from any role that still has it (databases from before), so those users stop
-- seeing the Admin button and the Users/Roles tabs. The Admin role needs no rows: it has every permission anyway.

DELETE rp
FROM role_permission rp
JOIN permission p ON p.permission_id = rp.permission_id
JOIN security_role r ON r.security_role_id = rp.security_role_id
WHERE p.code = 'MANAGE_USERS' AND r.code <> 'ADMIN';
