-- 022: a remark on each work of a request (owner, 2026-11-02): typed by staff in the Work window on the map, shown to the
-- contractor under that work (e.g. "leave the olive trees"). The work module's own table only; no existing data changes.

ALTER TABLE work_item
  ADD COLUMN remarks TEXT NULL COMMENT 'staff remark on this work, shown to the contractor; at most 2000 characters (checked by the app)';
