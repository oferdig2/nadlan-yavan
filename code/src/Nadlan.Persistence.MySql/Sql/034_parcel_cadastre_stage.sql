-- 034: each Parcel's Cadastre stage, and re-scraped polygons waiting for an Admin (brief
-- AI/Remarks/CodingAgent-PolygonStageAndUpdate-2026-11-26.md, owner and Alon 2026-11-23/26). New tables only: the parcel
-- table itself does not change (owner, 2026-11-26: new fields go in an extension table, not on parcel). 032 and 033 are held by
-- the deals branches (unmerged): renumber at merge time if needed, never reuse a number.
--
-- Stage: seen on the Cadastre map (the click request that answered: GETPST = InOperation, GETPSTKG = SurveyPost,
-- GETPSTKGPRO = PrePosting; source Polygon) or by the lawyer service (KAEK found = InOperation, "Δεν βρέθηκαν εγγραφές"
-- = NotOperating; source Lawyer). One row per Parcel; no row = unknown (entered by hand, or imported before this). The newest
-- sighting wins. Read with a LEFT JOIN (card, filter, Asset table). Staff only: never on presentations, public links or the
-- deals portal. ktima-records-v2 sets it from the lawyer run (brief §6): the same table.
CREATE TABLE parcel_cadastre_stage (
  parcel_id BIGINT NOT NULL,
  kaek VARCHAR(30) NOT NULL COMMENT 'the Parcel''s KAEK when the stage was seen (the lawyer run sets it by KAEK)',
  stage VARCHAR(16) NOT NULL COMMENT 'InOperation | SurveyPost | PrePosting | NotOperating',
  seen_utc DATETIME(3) NOT NULL COMMENT 'when it was seen; the newest sighting wins, whatever the source',
  source VARCHAR(10) NOT NULL COMMENT 'Polygon (the Cadastre map) | Lawyer (the lawyer service)',
  request_seen VARCHAR(12) NULL COMMENT 'Polygon: the click request that answered (GETPST | GETPSTKG | GETPSTKGPRO); NULL for Lawyer',
  updated_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)) COMMENT 'when this row was last written',
  PRIMARY KEY (parcel_id),
  KEY ix_parcel_cadastre_stage_kaek (kaek),
  KEY ix_parcel_cadastre_stage_stage (stage),
  CONSTRAINT fk_parcel_cadastre_stage_parcel FOREIGN KEY (parcel_id) REFERENCES parcel (parcel_id) ON DELETE CASCADE,
  CONSTRAINT ck_parcel_cadastre_stage_stage CHECK (stage IN ('InOperation', 'SurveyPost', 'PrePosting', 'NotOperating')),
  CONSTRAINT ck_parcel_cadastre_stage_source CHECK (source IN ('Polygon', 'Lawyer')),
  CONSTRAINT ck_parcel_cadastre_stage_request CHECK (request_seen IN ('GETPST', 'GETPSTKG', 'GETPSTKGPRO'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- A check of a Parcel already in GreekPlot that found something to decide (§3.3): the same KAEK with another polygon, another
-- KAEK at the point, or nothing there any more. A changed polygon never replaces ours by itself (OT/plot, Assets, work
-- requests and deals hang on the Parcel): an Admin accepts it (the normal polygon edit, the old one kept in the history) or
-- rejects it (only the decision is kept). A newer proposal for the same Parcel supersedes the pending one; at most one is
-- pending per Parcel (uq_parcel_geometry_proposal_pending).
CREATE TABLE parcel_geometry_proposal (
  parcel_geometry_proposal_id BIGINT NOT NULL AUTO_INCREMENT,
  parcel_id BIGINT NOT NULL,
  kind VARCHAR(16) NOT NULL COMMENT 'GeometryChanged | KaekMismatch | NotFound',
  status VARCHAR(12) NOT NULL DEFAULT 'Pending' COMMENT 'Pending | Accepted | Rejected | Superseded',
  kaek_seen VARCHAR(30) NULL COMMENT 'the KAEK the site answered at the point (NULL = nothing there)',
  stage_seen VARCHAR(16) NULL COMMENT 'the stage of the click request that was asked',
  geometry GEOMETRY SRID 4326 NULL COMMENT 'the Cadastre''s polygon (NULL for NotFound)',
  area_sqm DECIMAL(12,2) NULL COMMENT 'its area (planar, Greek grid): the official area if accepted',
  difference_sqm DECIMAL(12,2) NULL COMMENT 'symmetric difference with our polygon at the check, local metres',
  current_area_sqm DECIMAL(12,2) NULL COMMENT 'our polygon''s area at the check',
  parcel_updated_utc DATETIME(3) NOT NULL COMMENT 'the Parcel''s updated_utc at the check: saved since = compare again',
  seen_utc DATETIME(3) NOT NULL,
  created_utc DATETIME(3) NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  created_by_user_id BIGINT NULL,
  decided_utc DATETIME(3) NULL,
  decided_by_user_id BIGINT NULL COMMENT 'NULL with a decided_utc = superseded by a newer check',
  pending_parcel_id BIGINT AS (IF(status = 'Pending', parcel_id, NULL)) VIRTUAL COMMENT 'one pending proposal per Parcel',
  PRIMARY KEY (parcel_geometry_proposal_id),
  UNIQUE KEY uq_parcel_geometry_proposal_pending (pending_parcel_id),
  KEY ix_parcel_geometry_proposal_parcel (parcel_id, created_utc),
  KEY ix_parcel_geometry_proposal_status (status, created_utc),
  CONSTRAINT fk_parcel_geometry_proposal_parcel FOREIGN KEY (parcel_id) REFERENCES parcel (parcel_id) ON DELETE CASCADE,
  CONSTRAINT fk_parcel_geometry_proposal_created FOREIGN KEY (created_by_user_id) REFERENCES app_user (user_id) ON DELETE SET NULL,
  CONSTRAINT fk_parcel_geometry_proposal_decided FOREIGN KEY (decided_by_user_id) REFERENCES app_user (user_id) ON DELETE SET NULL,
  CONSTRAINT ck_parcel_geometry_proposal_kind CHECK (kind IN ('GeometryChanged', 'KaekMismatch', 'NotFound')),
  CONSTRAINT ck_parcel_geometry_proposal_status CHECK (status IN ('Pending', 'Accepted', 'Rejected', 'Superseded')),
  CONSTRAINT ck_parcel_geometry_proposal_stage CHECK (stage_seen IN ('InOperation', 'SurveyPost', 'PrePosting')),
  CONSTRAINT ck_parcel_geometry_proposal_shape CHECK (kind <> 'GeometryChanged' OR geometry IS NOT NULL)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
