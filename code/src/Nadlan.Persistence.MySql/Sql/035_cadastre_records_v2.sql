-- 035: Cadastre records V2 (brief AI/Remarks/CodingAgent-KtimaRecordsV2-2026-11-26.md, owner's decisions of 2026-11-26).
-- 032 and 033 are held by the deals branches and 034 by polygon-stage (all unmerged): renumber at merge time if needed,
-- never reuse a number.
--
-- The owner, 2026-11-26: keep everything about owners and parties (full name, ΑΦΜ for people too, parents' names, birth
-- date and place, ID card, address, right type and share); store the two extracts (descriptive KF, spatial KD) and the
-- summary of the last sale or auction; Admin only, every open of a document logged; no retention rule. And (same day):
-- everything new lives in extension tables, every field optional - no column is added to parcel or any other core table.
-- These are the Cadastre's records ("as listed by the Cadastre"), separate from parcel_legal_owner.

-- ---- owners and parties: every field, the ΑΦΜ of people too ---------------------------------------------------------

-- The 24 Nov rule "never a person's ΑΦΜ" is reversed: the CHECKs go, the column is renamed, its index stays (it follows the
-- column), and "other parcels of this owner" now works for people too. owner_kind / party_kind stay, for classifying only.
ALTER TABLE cadastre_owner DROP CHECK ck_cadastre_owner_tax;
ALTER TABLE cadastre_owner RENAME COLUMN entity_tax_id TO tax_id;
ALTER TABLE cadastre_owner
  MODIFY COLUMN tax_id VARCHAR(12) NULL COMMENT 'ΑΦΜ (tax number) of a person or an entity (owner, 2026-11-26)',
  ADD COLUMN father_name VARCHAR(200) NULL COMMENT 'father''s name (record page: first name; extract/summary: full)',
  ADD COLUMN mother_name VARCHAR(200) NULL,
  ADD COLUMN birth_date DATE NULL COMMENT 'the site''s 01/01/0001 placeholder = NULL',
  ADD COLUMN birth_place VARCHAR(200) NULL,
  ADD COLUMN id_card_number VARCHAR(30) NULL COMMENT 'Α.Δ.Τ.',
  ADD COLUMN id_card_date DATE NULL,
  ADD COLUMN id_card_authority VARCHAR(200) NULL,
  ADD COLUMN right_type VARCHAR(100) NULL COMMENT 'ΠΛΗΡΗΣ | ΨΙΛΗ | ΕΠΙΚΑΡΠΙΑ ...',
  ADD COLUMN source VARCHAR(20) NULL COMMENT 'RecordPage | DescriptiveExtract | Summary: where the row came from',
  ADD CONSTRAINT ck_cadastre_owner_source CHECK (source IN ('RecordPage', 'DescriptiveExtract', 'Summary'));

ALTER TABLE cadastre_act_party DROP CHECK ck_cadastre_act_party_tax;
ALTER TABLE cadastre_act_party RENAME COLUMN entity_tax_id TO tax_id;
ALTER TABLE cadastre_act_party
  MODIFY COLUMN tax_id VARCHAR(12) NULL COMMENT 'ΑΦΜ (tax number) of a person or an entity (owner, 2026-11-26)',
  ADD COLUMN father_name VARCHAR(200) NULL,
  ADD COLUMN mother_name VARCHAR(200) NULL,
  ADD COLUMN birth_date DATE NULL COMMENT 'the site''s 01/01/0001 placeholder = NULL',
  ADD COLUMN birth_place VARCHAR(200) NULL,
  ADD COLUMN id_card_number VARCHAR(30) NULL,
  ADD COLUMN id_card_date DATE NULL,
  ADD COLUMN id_card_authority VARCHAR(200) NULL,
  ADD COLUMN right_type VARCHAR(100) NULL COMMENT 'ΠΛΗΡΗΣ | ΨΙΛΗ | ΕΠΙΚΑΡΠΙΑ ...',
  ADD COLUMN source VARCHAR(20) NULL COMMENT 'RecordPage | DescriptiveExtract | Summary',
  ADD KEY ix_cadastre_act_party_tax (tax_id),
  ADD CONSTRAINT ck_cadastre_act_party_source CHECK (source IN ('RecordPage', 'DescriptiveExtract', 'Summary'));

-- ---- what the descriptive extract (KF) and the diagram (KD) say about the unit ----------------------------------------

-- NotRead: a unit over the run's unit cap (default 10 per KAEK): only its row in the search was kept.
ALTER TABLE cadastre_fetch DROP CHECK ck_cadastre_fetch_status;
ALTER TABLE cadastre_fetch
  ADD COLUMN measured_area_sqm DECIMAL(12,2) NULL COMMENT 'ΕΜΒΑΔΟΝ (measured) from the descriptive extract',
  ADD COLUMN remarks_text VARCHAR(1000) NULL COMMENT 'ΠΑΡΑΤΗΡΗΣΕΙΣ as written in the extract',
  ADD COLUMN ot_text VARCHAR(30) NULL COMMENT 'the OT number the remarks give (Ο.Τ. 18 = 18): a suggestion only, never written to the parcel',
  ADD COLUMN extract_status VARCHAR(12) NULL COMMENT 'Read | NeedsOcr | NotFetched | Error (the descriptive extract); NULL = V1 record',
  ADD COLUMN property_kind VARCHAR(60) NULL COMMENT 'ΓΕΩΤΕΜΑΧΙΟ, ΟΡΙΖΟΝΤΙΟΣ ΙΔΙΟΚΤΗΣΙΑ ...',
  ADD COLUMN office_text VARCHAR(200) NULL COMMENT 'ΚΤΗΜΑΤΟΛΟΓΙΚΟ ΓΡΑΦΕΙΟ / ΑΡΜΟΔΙΟΤΗΤΑ',
  ADD COLUMN map_sheet VARCHAR(60) NULL COMMENT 'ΦΥΛΛΟ ΧΑΡΤΟΥ of the diagram',
  ADD COLUMN neighbour_kaeks VARCHAR(2000) NULL COMMENT 'neighbours'' KAEKs written on the diagram, space separated (when it has text)',
  ADD CONSTRAINT ck_cadastre_fetch_status CHECK (status IN ('Found', 'NotOperating', 'NeedsOcr', 'NotRead', 'Error')),
  ADD CONSTRAINT ck_cadastre_fetch_extract CHECK (extract_status IN ('Read', 'NeedsOcr', 'NotFetched', 'Error'));

-- Extract: a registration read from the descriptive extract (no price there, except where the extract shows one).
ALTER TABLE cadastre_act DROP CHECK ck_cadastre_act_source;
ALTER TABLE cadastre_act
  ADD COLUMN reg_date DATE NULL COMMENT 'Ημ/νία Καταχώρισης (the registration''s date; act_date is the deed''s)',
  ADD COLUMN section VARCHAR(12) NULL COMMENT 'for Extract acts: Initial (ΑΡΧΙΚΕΣ) | Later (ΜΕΤΑΓΕΝΕΣΤΕΡΕΣ) | Servitude (ΔΟΥΛΕΙΕΣ) | Other',
  ADD COLUMN right_text VARCHAR(100) NULL COMMENT 'the right registered, e.g. ΚΥΡΙΟΤΗΤΑ ΨΙΛΗ, ΕΠΙΚΑΡΠΙΑ',
  ADD COLUMN document_text VARCHAR(300) NULL COMMENT 'Έγγραφο, e.g. ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ, 4876 - 10/7/2024',
  ADD COLUMN issuer_text VARCHAR(300) NULL COMMENT 'the notary or authority and town',
  ADD COLUMN reg_status_text VARCHAR(100) NULL COMMENT 'e.g. ΟΡΙΣΤΙΚΗ ΚΑΤΑΧΩΡΙΣΗ',
  ADD CONSTRAINT ck_cadastre_act_source CHECK (source IN ('Summary', 'OldTitle', 'RecordTitle', 'Extract')),
  ADD CONSTRAINT ck_cadastre_act_section CHECK (section IN ('Initial', 'Later', 'Servitude', 'Other'));

-- ---- the stored documents ---------------------------------------------------------------------------------------------

-- The PDFs themselves are in the private storage (S3) under cadastre/{KAEK}/{v}-{h}/ (never a folder starting with "_": the
-- CDN blocks those). Admin only; every open is written to activity (entity CadastreDocument, action Opened). Never on
-- presentations, public links or the deals portal: they are not file_attachment rows and no other API lists them.
-- Deleting trial data: delete the rows AND the storage folder cadastre/.
CREATE TABLE cadastre_document (
  cadastre_document_id BIGINT NOT NULL AUTO_INCREMENT,
  cadastre_fetch_id BIGINT NOT NULL COMMENT 'the unit''s row of the snapshot it was fetched with',
  kaek CHAR(12) NOT NULL,
  unit_v INT NOT NULL,
  unit_h INT NOT NULL,
  kind VARCHAR(20) NOT NULL COMMENT 'DescriptiveExtract (KF) | SpatialExtract (KD, the diagram) | Summary (the last sale''s)',
  reg_no VARCHAR(20) NULL COMMENT 'a summary''s registration, e.g. 5413 of 5413/2024',
  reg_year INT NULL,
  file_name VARCHAR(200) NOT NULL COMMENT 'e.g. 460990208019-0-0-KF.pdf',
  size_bytes BIGINT NOT NULL,
  sha256 CHAR(64) NOT NULL,
  storage_key VARCHAR(500) NOT NULL,
  fetched_utc DATETIME(3) NOT NULL,
  fetched_by_user_id BIGINT NULL,
  text_readable TINYINT(1) NOT NULL DEFAULT 0 COMMENT '0 = a scan or unread layout (NeedsOcr)',
  PRIMARY KEY (cadastre_document_id),
  UNIQUE KEY uq_cadastre_document_file (cadastre_fetch_id, kind, sha256),
  KEY ix_cadastre_document_kaek (kaek, fetched_utc),
  CONSTRAINT fk_cadastre_document_fetch FOREIGN KEY (cadastre_fetch_id) REFERENCES cadastre_fetch (cadastre_fetch_id) ON DELETE CASCADE,
  CONSTRAINT fk_cadastre_document_user FOREIGN KEY (fetched_by_user_id) REFERENCES app_user (user_id) ON DELETE SET NULL,
  CONSTRAINT ck_cadastre_document_kind CHECK (kind IN ('DescriptiveExtract', 'SpatialExtract', 'Summary'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- ---- each parcel's Cadastre stage (an extension table, owner 2026-11-26) ---------------------------------------------

-- One row per Parcel; no row = unknown (read it with a LEFT JOIN). The newest sighting wins, whatever the source. Set by the
-- lawyer records run (a KAEK found = InOperation, "Δεν βρέθηκαν εγγραφές" = NotOperating) and by the polygon importer
-- (branch polygon-stage: GETPST = InOperation, GETPSTKG = SurveyPost, GETPSTKGPRO = PrePosting). Staff only.
-- The polygon-stage branch creates the same table (its 034, commit c57125f): this definition is a copy of it, column for column,
-- and IF NOT EXISTS makes the second of the two a no-op. Reconcile the two at merge (brief PolygonStageAndUpdate §6).
CREATE TABLE IF NOT EXISTS parcel_cadastre_stage (
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
