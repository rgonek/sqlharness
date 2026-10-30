### Remaining finding 2: NOT ADDRESSED

- LinuxProfileMetadata.cs:29-38 treats a zero-length listxattr result as proof of no extended attributes. Linux listxattr can omit attributes inaccessible to the calling process; zero therefore does not establish absence. An atomic replacement can silently discard such metadata. See https://www.man7.org/linux/man-pages/man2/listxattr.2.html. In addition, StatxData at LinuxProfileMetadata.cs:15-22 ignores stx_attributes and its mask: inode protections such as fs-verity and nodump are not xattrs and can be lost with replacement. See https://man7.org/linux/man-pages/man2/statx.2.html. Thus the stated fail-closed guarantee is not established; preserve or prove the security-relevant metadata, or refuse Linux saves when that proof cannot be made.
- Owner, group and basic mode mismatches now fail closed (LinuxProfileMetadata.cs:25-27; ProfileWriter.cs:67-76); visible extended attributes, unsupported syscalls, and macOS also refuse saving. The Windows restricted DACL path remains unchanged and was tested in round 1.

### New breakage in scoped diff

- No separate issue identified. Security metadata omissions above are the continuing original finding.

### Evidence and assessment

- Reported Windows focused run: 21 passed (plans/124-evidence/logs/task2-review2-final.log). Non-Windows conditional test bodies returned early and must not be counted as Linux/macOS execution. Linux ABI and filesystem behavior remain unverified; no tests rerun.
- **Task quality: Needs fixes.** Fail-closed availability restrictions are permissible under the controller ruling, but zero visible xattrs and three stat fields do not prove all security metadata is absent or preserved. The explicitly documented external-writer race remains a separate design limitation.