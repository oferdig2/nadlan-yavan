-- 012: OT / plot data entry (customer change request #1, review of 2026-10-07).
-- Who last entered or changed a Parcel's OT / plot and when, written in the same UPDATE as the values (not only in the
-- best-effort history). No foreign key: like activity.user_id, the id stays when a user is deleted.
-- Search keys: OT and plot as one comparable text each - number and extension joined, upper case, Greek capitals
-- folded to the Latin look-alikes, spaces . / _ - removed, leading zeros dropped: "047 α", "47-A", "47Α" and "47A" are
-- all 47A. *_base is the leading number ("47"), or the whole key if it doesn't start with one. Same rule as
-- Nadlan.Core.Parcels.ParcelNumberKey (search input) - change both together. Indexed (base, key): "47" finds every 47,
-- "47A" only 47 + A, without scanning the table.

ALTER TABLE parcel
  ADD COLUMN ot_plot_by_user_id BIGINT NULL COMMENT 'who last entered/changed OT or plot (app_user.user_id); NULL = import or before 012' AFTER plot_ext,
  ADD COLUMN ot_plot_updated_utc DATETIME(3) NULL COMMENT 'when OT or plot last changed' AFTER ot_plot_by_user_id,
  ADD COLUMN ot_key VARCHAR(64) GENERATED ALWAYS AS (NULLIF(REGEXP_REPLACE(
    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
      REGEXP_REPLACE(UPPER(CONCAT(COALESCE(ot, ''), COALESCE(ot_ext, ''))), '[[:space:]./_-]+', ''),
      'Α', 'A'), 'Β', 'B'), 'Ε', 'E'), 'Ζ', 'Z'), 'Η', 'H'), 'Ι', 'I'), 'Κ', 'K'), 'Μ', 'M'), 'Ν', 'N'), 'Ο', 'O'), 'Ρ', 'P'), 'Τ', 'T'), 'Υ', 'Y'), 'Χ', 'X'),
    '^0+([0-9])', '$1'), '')) STORED COMMENT 'search key of OT + ext (see header)',
  ADD COLUMN ot_base VARCHAR(64) GENERATED ALWAYS AS (COALESCE(REGEXP_SUBSTR(ot_key, '^[0-9]+'), ot_key)) STORED,
  ADD COLUMN plot_key VARCHAR(64) GENERATED ALWAYS AS (NULLIF(REGEXP_REPLACE(
    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
      REGEXP_REPLACE(UPPER(CONCAT(COALESCE(plot_number, ''), COALESCE(plot_ext, ''))), '[[:space:]./_-]+', ''),
      'Α', 'A'), 'Β', 'B'), 'Ε', 'E'), 'Ζ', 'Z'), 'Η', 'H'), 'Ι', 'I'), 'Κ', 'K'), 'Μ', 'M'), 'Ν', 'N'), 'Ο', 'O'), 'Ρ', 'P'), 'Τ', 'T'), 'Υ', 'Y'), 'Χ', 'X'),
    '^0+([0-9])', '$1'), '')) STORED COMMENT 'search key of plot + ext (see header)',
  ADD COLUMN plot_base VARCHAR(64) GENERATED ALWAYS AS (COALESCE(REGEXP_SUBSTR(plot_key, '^[0-9]+'), plot_key)) STORED,
  ADD KEY ix_parcel_ot_key (ot_base, ot_key),
  ADD KEY ix_parcel_plot_key (plot_base, plot_key),
  ADD KEY ix_parcel_ot_plot_by (ot_plot_by_user_id, ot_plot_updated_utc);

-- "Everything user X did today": history by user, newest first. 007 indexed user_id alone; with the time it serves the
-- date range and the order too.
ALTER TABLE activity
  DROP KEY ix_activity_user,
  ADD KEY ix_activity_user (user_id, created_utc);
