# Dependency notice provenance

The application source is MIT licensed. Dependencies keep their own licenses.
The inventory covers all 496 registry packages in Cargo.lock, including optional
and target-only packages; this does not mean every package is linked everywhere.

For this checkpoint:

- 493 packages have notices collected from the exact published package or its
  recorded source commit. Rust distribution notices are tracked separately.
- `drm-fourcc` 2.2.0 declares MIT but its release commit has no standalone notice.
  The included LICENSE comes from the same project's later commit
  `1ad1a978a9aaa78ff0016cc7013787e4d1d68682`.
- `hexf-parse` 0.2.1 declares CC0-1.0 but its release commit has no standalone
  notice. The included LICENSE comes from the same project's later commit
  `0e69bc4f63895feb51e9b738a8a2d4ec9398957b`. The parse/LICENSE entry there refers to
  the root license. Both later-source cases remain explicitly identified in the
  inventory; they are not attributed to the published release commits.
- `r-efi` 5.3.0 and 6.0.0 put their actual license text and copyrights in AUTHORS,
  which the older root filename search missed. Those exact-commit files are now
  included. The selected alternative for this distribution is MIT.
- **One upstream notice gap remains:** Apple-only `dispatch` 0.2.0 declares MIT in
  its published Cargo.toml, but neither recorded commit
  `82d6c7a5b75dc0c71c3f46f87bb6c16a476f7748` nor inspected upstream HEAD
  `f540a2d8ccaebf0e87f5805033b9e287e8d01ba5` contains a standalone license notice.
  The exact package declaration is retained. No copyright holder/year or substitute
  upstream notice is invented. Resolve this before distributing Apple binaries.

Each copied notice has a SHA-256 and crate path or pinned upstream URL in
`third-party/portable/inventory.json`. Run `python scripts/verify-notices.py` to
check lockfile coverage, path containment and all recorded bytes. The collector
preserves previously verified, explicitly attributed upstream supplements.

This records source provenance and remaining exceptions. It is not an independent
legal opinion, and it does not certify redistribution of unbuilt/untested targets.
