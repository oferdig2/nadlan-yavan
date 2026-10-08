-- 015: the version an app_config row had before each change (review of 2026-10-08), so a bad settings save is a
-- one-command undo even when the app no longer starts: sudo nadlan-db config undo ms:host (7-server-admin.ps1 has it).
-- Written by every writer of app_config (Settings page, nadlan-db config set/remove/undo, startup adding new defaults).
-- Holds secrets like app_config itself does; it is never shown by the app.

CREATE TABLE app_config_history (
  history_id BIGINT NOT NULL AUTO_INCREMENT,
  config_key VARCHAR(200) NOT NULL,
  json_text LONGTEXT NOT NULL COMMENT 'the row as it was before the change',
  version_utc_ms BIGINT NOT NULL COMMENT 'its updated_utc_ms',
  replaced_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  replaced_by VARCHAR(200) NULL COMMENT 'who / what changed it, e.g. "settings page: ofer@...", "nadlan-db config set"',
  PRIMARY KEY (history_id),
  KEY ix_app_config_history_key (config_key, history_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
