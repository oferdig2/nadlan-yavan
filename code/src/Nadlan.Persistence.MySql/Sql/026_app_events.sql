-- 026: in-house analytics (owner, 2026-11-09 / 2026-11-10) - a generic event module (Mixpanel-like): one row per event with
-- its own JSON properties, the visit's context once per session. First source: anonymous visitors of public presentation
-- links (show.html). Kept forever for now (no clean-up job). New tables and one permission only.

-- The visit (one browser tab's session): where it came from, the device, the IP and what the IP says about the place.
CREATE TABLE app_event_context (
  context_id BIGINT NOT NULL AUTO_INCREMENT,
  session_id VARCHAR(40) NOT NULL COMMENT 'made by the page: one visit',
  visitor_id VARCHAR(40) NOT NULL COMMENT 'made by the page: one browser over time (localStorage)',
  source VARCHAR(32) NOT NULL COMMENT 'present_public now; later present_signed_in, map, ...',
  presentation_link_id BIGINT NULL COMMENT 'from the link token, never from the request body',
  first_utc DATETIME(3) NOT NULL COMMENT 'first event of the session',
  last_utc DATETIME(3) NOT NULL COMMENT 'last event of the session',
  ip VARCHAR(45) NULL COMMENT 'the real client IP (behind nginx: X-Forwarded-For), IPv4 or IPv6',
  geo_country CHAR(2) NULL COMMENT 'from the IP (MaxMind GeoLite2); NULL until known',
  geo_region VARCHAR(100) NULL,
  geo_city VARCHAR(100) NULL,
  geo_lat DECIMAL(9,6) NULL COMMENT 'approximate',
  geo_lng DECIMAL(9,6) NULL,
  geo_accuracy_km INT NULL,
  geo_asn INT NULL COMMENT 'network number',
  geo_org VARCHAR(200) NULL COMMENT 'network / ISP, e.g. Cosmote Mobile',
  user_agent VARCHAR(500) NULL COMMENT 'raw',
  browser VARCHAR(50) NULL,
  browser_version VARCHAR(30) NULL,
  os VARCHAR(50) NULL,
  os_version VARCHAR(30) NULL,
  device_type VARCHAR(10) NULL COMMENT 'phone / tablet / desktop',
  device_model VARCHAR(100) NULL,
  screen_w INT NULL,
  screen_h INT NULL,
  viewport_w INT NULL,
  viewport_h INT NULL,
  pixel_ratio DECIMAL(4,2) NULL,
  orientation VARCHAR(10) NULL COMMENT 'portrait / landscape',
  touch TINYINT(1) NULL,
  language VARCHAR(35) NULL,
  languages VARCHAR(200) NULL COMMENT 'comma separated, as navigator.languages',
  time_zone VARCHAR(64) NULL,
  referrer VARCHAR(1000) NULL COMMENT 'document.referrer as the page saw it; NULL when empty',
  referrer_host VARCHAR(255) NULL,
  utm_source VARCHAR(100) NULL,
  utm_medium VARCHAR(100) NULL,
  utm_campaign VARCHAR(100) NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (context_id),
  UNIQUE KEY uq_app_event_context_session (session_id),
  KEY ix_app_event_context_link (presentation_link_id, first_utc),
  KEY ix_app_event_context_visitor (visitor_id, first_utc),
  CONSTRAINT fk_app_event_context_link FOREIGN KEY (presentation_link_id) REFERENCES presentation_link (presentation_link_id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- One row per event: a snake_case name, the time it happened (the browser's, corrected), what it was about, and its own
-- properties ({"index": 3, "of": 12}). Unknown names are kept: the reports read the names they know.
CREATE TABLE app_event (
  app_event_id BIGINT NOT NULL AUTO_INCREMENT,
  event VARCHAR(64) NOT NULL COMMENT 'snake_case verb, e.g. asset_viewed',
  occurred_utc DATETIME(3) NOT NULL COMMENT 'the browser time, moved by its clock difference to ours',
  received_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  source VARCHAR(32) NOT NULL,
  session_id VARCHAR(40) NOT NULL,
  visitor_id VARCHAR(40) NOT NULL,
  user_id BIGINT NULL COMMENT 'a signed-in user - always NULL for now (no staff recording yet)',
  presentation_link_id BIGINT NULL COMMENT 'from the link token, never from the request body',
  subject_type VARCHAR(32) NULL COMMENT 'Asset, later Parcel, Portfolio, File',
  subject_id BIGINT NULL,
  properties JSON NULL COMMENT 'custom per event, at most 2 KB',
  context_id BIGINT NOT NULL,
  PRIMARY KEY (app_event_id),
  KEY ix_app_event_link (presentation_link_id, occurred_utc),
  KEY ix_app_event_subject (subject_type, subject_id, occurred_utc),
  KEY ix_app_event_visitor (visitor_id, occurred_utc),
  KEY ix_app_event_event (event, occurred_utc),
  KEY ix_app_event_session (session_id),
  KEY ix_app_event_context (context_id),
  CONSTRAINT fk_app_event_link FOREIGN KEY (presentation_link_id) REFERENCES presentation_link (presentation_link_id) ON DELETE SET NULL,
  CONSTRAINT fk_app_event_context FOREIGN KEY (context_id) REFERENCES app_event_context (context_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- A batch is stored once: the page sends it again when it got no answer (as work_track_batch). Kept for now (owner: forever).
CREATE TABLE app_event_batch (
  session_id VARCHAR(40) NOT NULL,
  batch_id VARCHAR(40) NOT NULL,
  received_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  PRIMARY KEY (session_id, batch_id),
  KEY ix_app_event_batch_received (received_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT INTO permission (code, name, description, scope, resource_type, group_name, sort_order)
SELECT 'VIEW_ANALYTICS', 'Analytics', 'See who opened public links and what they looked at.', 'Role', NULL, 'Pages', 6
WHERE NOT EXISTS (SELECT 1 FROM permission WHERE code = 'VIEW_ANALYTICS');

-- Admin has every permission already; the Data person gets it (owner, 2026-11-10: "Admin and Data person only").
INSERT INTO role_permission (security_role_id, permission_id)
SELECT r.security_role_id, p.permission_id
FROM security_role r
JOIN permission p ON p.code = 'VIEW_ANALYTICS'
WHERE r.code = 'DATA_PERSON'
  AND NOT EXISTS (SELECT 1 FROM role_permission rp WHERE rp.security_role_id = r.security_role_id AND rp.permission_id = p.permission_id);
