-- 001: Application configuration, same shape and key scheme as Futuristic SaaS (sql/app-config-mysql-v1.sql).
-- Keys:
--   common:application  settings shared by every Nadlan process
--   ms:<process>        settings for one process, e.g. ms:host (the web app), ms:import
-- Each row holds one JSON document that is flattened into IConfiguration (A:B:C keys).
-- Rows are seeded from the process's appsettings.json the first time it starts; after that the DB wins.

CREATE TABLE app_config (
  config_key VARCHAR(200) NOT NULL,
  json_text LONGTEXT NOT NULL,
  updated_utc_ms BIGINT NOT NULL,
  PRIMARY KEY (config_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
