-- Second Admin (user request 2026-10-05). Like the first: no password - signs in with Google once Nadlan:Auth:Google is
-- configured, or gets a password from an Admin (Admin > Users) or with the DB tool (user set-password).
INSERT INTO app_user (email, display_name, security_role_id)
SELECT 'alon.schwarz@gmail.com', 'Alon', security_role_id FROM security_role WHERE code = 'ADMIN'
  AND NOT EXISTS (SELECT 1 FROM app_user WHERE email = 'alon.schwarz@gmail.com');
