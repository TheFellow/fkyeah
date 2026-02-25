#!/usr/bin/env python3
"""
Sprint Ledger Manager for Megaplan

Manages sprint status tracking in a TSV file format.

Usage:
    python3 ledger.py stats              # Show overview statistics
    python3 ledger.py current            # Show current in-progress sprint
    python3 ledger.py next               # Get next planned sprint
    python3 ledger.py add <num> <title>  # Add a new sprint
    python3 ledger.py start <num>        # Mark sprint as in_progress
    python3 ledger.py complete <num>     # Mark sprint as completed
    python3 ledger.py skip <num>         # Mark sprint as skipped
    python3 ledger.py list [--status X]  # List sprints with optional filter
    python3 ledger.py sync               # Sync ledger from SPRINT-*.md documents
"""

import sys
import os
import re
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Optional

LEDGER_FILE = Path(__file__).parent / "ledger.tsv"
SPRINTS_DIR = Path(__file__).parent

@dataclass
class SprintEntry:
    VALID_STATUSES = ["planned", "in_progress", "completed", "skipped"]

    sprint_id: str
    title: str
    status: str
    created_at: str
    updated_at: str

    def to_tsv(self) -> str:
        return f"{self.sprint_id}\t{self.title}\t{self.status}\t{self.created_at}\t{self.updated_at}"

    @classmethod
    def from_tsv(cls, line: str) -> "SprintEntry":
        parts = line.strip().split("\t")
        if len(parts) != 5:
            raise ValueError(f"Invalid TSV line: {line}")
        return cls(*parts)


class SprintLedger:
    HEADER = "sprint_id\ttitle\tstatus\tcreated_at\tupdated_at"

    def __init__(self, path: Path = LEDGER_FILE):
        self.path = path
        self.entries: list[SprintEntry] = []

    def load(self) -> "SprintLedger":
        if not self.path.exists():
            return self
        with open(self.path, "r") as f:
            lines = f.readlines()
        for line in lines[1:]:  # Skip header
            if line.strip():
                self.entries.append(SprintEntry.from_tsv(line))
        return self

    def save(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with open(self.path, "w") as f:
            f.write(self.HEADER + "\n")
            for entry in sorted(self.entries, key=lambda e: e.sprint_id):
                f.write(entry.to_tsv() + "\n")

    def add(self, sprint_id: str, title: str, status: str = "planned") -> SprintEntry:
        now = datetime.now().isoformat()
        entry = SprintEntry(sprint_id, title, status, now, now)
        self.entries.append(entry)
        return entry

    def get(self, sprint_id: str) -> Optional[SprintEntry]:
        for entry in self.entries:
            if entry.sprint_id == sprint_id:
                return entry
        return None

    def update_status(self, sprint_id: str, status: str) -> SprintEntry:
        if status not in SprintEntry.VALID_STATUSES:
            raise ValueError(f"Invalid status: {status}")
        entry = self.get(sprint_id)
        if not entry:
            raise ValueError(f"Sprint not found: {sprint_id}")
        entry.status = status
        entry.updated_at = datetime.now().isoformat()
        return entry

    def get_next_number(self) -> int:
        if not self.entries:
            return 1
        max_num = max(int(e.sprint_id) for e in self.entries)
        return max_num + 1

    def count_by_status(self) -> dict[str, int]:
        counts = {s: 0 for s in SprintEntry.VALID_STATUSES}
        for entry in self.entries:
            counts[entry.status] = counts.get(entry.status, 0) + 1
        return counts

    def sync_from_docs(self) -> list[str]:
        changes = []
        if not SPRINTS_DIR.exists():
            return changes

        title_pattern = re.compile(r"^# Sprint[: ]+(.+)$", re.MULTILINE)

        for md_file in sorted(SPRINTS_DIR.glob("SPRINT-*.md")):
            match = re.match(r"SPRINT-(\d+)\.md", md_file.name)
            if not match:
                continue

            sprint_id = match.group(1).zfill(3)
            title = f"Sprint {sprint_id}"

            try:
                content = md_file.read_text()
            except Exception:
                content = ""

            title_match = title_pattern.search(content)
            if title_match:
                title = title_match.group(1).strip()

            entry = self.get(sprint_id)
            if not entry:
                self.add(sprint_id, title)
                changes.append(f"Added: {sprint_id} - {title}")
                continue

            if entry.title != title:
                entry.title = title
                entry.updated_at = datetime.now().isoformat()
                changes.append(f"Updated title: {sprint_id} - {title}")

        return changes


def cmd_stats(ledger: SprintLedger):
    counts = ledger.count_by_status()
    total = len(ledger.entries)
    print(f"Sprint Ledger Statistics")
    print(f"========================")
    print(f"Total sprints: {total}")
    for status, count in counts.items():
        print(f"  {status}: {count}")
    print(f"\nNext sprint number: {ledger.get_next_number():03d}")


def cmd_current(ledger: SprintLedger):
    for entry in ledger.entries:
        if entry.status == "in_progress":
            print(f"{entry.sprint_id}\t{entry.title}")
            return
    print("No sprint currently in progress")


def cmd_next(ledger: SprintLedger):
    for entry in sorted(ledger.entries, key=lambda e: e.sprint_id):
        if entry.status == "planned":
            print(f"{entry.sprint_id}\t{entry.title}")
            return
    print("No planned sprints remaining")


def cmd_add(ledger: SprintLedger, sprint_num: str, title: str):
    sprint_id = f"{int(sprint_num):03d}"
    if ledger.get(sprint_id):
        print(f"Error: Sprint {sprint_id} already exists", file=sys.stderr)
        sys.exit(1)
    entry = ledger.add(sprint_id, title)
    ledger.save()
    print(f"Added sprint {sprint_id}: {title}")


def cmd_start(ledger: SprintLedger, sprint_num: str):
    sprint_id = f"{int(sprint_num):03d}"
    entry = ledger.update_status(sprint_id, "in_progress")
    ledger.save()
    print(f"Started sprint {sprint_id}: {entry.title}")


def cmd_complete(ledger: SprintLedger, sprint_num: str):
    sprint_id = f"{int(sprint_num):03d}"
    entry = ledger.update_status(sprint_id, "completed")
    ledger.save()
    print(f"Completed sprint {sprint_id}: {entry.title}")


def cmd_skip(ledger: SprintLedger, sprint_num: str):
    sprint_id = f"{int(sprint_num):03d}"
    entry = ledger.update_status(sprint_id, "skipped")
    ledger.save()
    print(f"Skipped sprint {sprint_id}: {entry.title}")


def cmd_list(ledger: SprintLedger, status_filter: Optional[str] = None):
    for entry in sorted(ledger.entries, key=lambda e: e.sprint_id):
        if status_filter and entry.status != status_filter:
            continue
        print(f"{entry.sprint_id}\t{entry.status}\t{entry.title}")


def cmd_sync(ledger: SprintLedger):
    changes = ledger.sync_from_docs()
    ledger.save()
    if changes:
        print("Sync complete:")
        for change in changes:
            print(f"  {change}")
    else:
        print("Sync complete (no changes)")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    ledger = SprintLedger().load()
    cmd = sys.argv[1]

    if cmd == "stats":
        cmd_stats(ledger)
    elif cmd == "current":
        cmd_current(ledger)
    elif cmd == "next":
        cmd_next(ledger)
    elif cmd == "add" and len(sys.argv) >= 4:
        cmd_add(ledger, sys.argv[2], " ".join(sys.argv[3:]))
    elif cmd == "start" and len(sys.argv) >= 3:
        cmd_start(ledger, sys.argv[2])
    elif cmd == "complete" and len(sys.argv) >= 3:
        cmd_complete(ledger, sys.argv[2])
    elif cmd == "skip" and len(sys.argv) >= 3:
        cmd_skip(ledger, sys.argv[2])
    elif cmd == "list":
        status = sys.argv[3] if len(sys.argv) > 3 and sys.argv[2] == "--status" else None
        cmd_list(ledger, status)
    elif cmd == "sync":
        cmd_sync(ledger)
    else:
        print(f"Unknown command: {cmd}", file=sys.stderr)
        print(__doc__)
        sys.exit(1)


if __name__ == "__main__":
    main()
