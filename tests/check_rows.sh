#!/bin/sh
# Row coverage of the section 3.1 transition tables (P7 "Check").
#
#   sh tests/check_rows.sh docs/target-architecture.md build/test-*.log
#
# The spec is the list of rows: every "| ID |" line of the C-A and C-C tables
# and their invariants. The "Row coverage" table of the same document says
# which tests characterize each row, by the stable IDs the tests print
# ("ok <id> - <name>", tests/check.h). This script holds the three together
# and fails when
#   - a spec row has no line in the coverage table, or the table names a row
#     the spec does not have;
#   - a test the table names did not run and pass;
#   - a test whose ID is a row ("CA.G2", "CA.G2/variant") is missing from
#     that row's line, or names a row the spec does not have;
#   - a row listed as "none" (the explicit allowlist of rows without a test)
#     has gained a test, or gives no reason.
# It prints the row -> test table it verified.
[ $# -ge 2 ] || { echo "usage: $0 <spec.md> <test log>..." >&2; exit 2; }
spec=$1; shift
LC_ALL=C awk -v spec="$spec" '
function trim(s) { gsub(/^[ \t`]+|[ \t`]+$/,"",s); return s }
function fail(message) { print "check_rows: " message; failed=1 }
FILENAME==spec {
    gsub(/\342\200\221/,"-")                      # the document writes C-A with U+2011
    if ($0 ~ /^#### /) {
        table=""
        if ($0 ~ /^#### C-A Lane operation/) table="CA"
        if ($0 ~ /^#### C-C Persistent recovery latch/) table="CC"
        if ($0 ~ /^#### Row coverage/) table="coverage"
        next
    }
    if (table=="" || $0 !~ /^\| /) next
    n=split($0,cell,"|"); id=trim(cell[2])
    if (table=="coverage") {
        if (id !~ /^C[AC]\.[A-Z]+-?[0-9]+$/) next
        if (id in listed) fail(id " is listed twice in the coverage table")
        listed[id]=trim(cell[3]); note[id]=trim(cell[4]); order[++rows]=id
    } else if (id ~ /^[A-Z]+-?[0-9]+$/) required[table "." id]=1
    next
}
/^ok / { passed[$2]=1; if ($0 ~ / # expected to change: /) changing[$2]=1 }
END {
    for (id in required) { ++total; if (!(id in listed)) fail("spec row " id " has no line in the coverage table") }
    for (i=1;i<=rows;++i) {
        id=order[i]
        if (!(id in required)) { fail("the coverage table lists " id ", which is not a spec row"); continue }
        if (listed[id]=="none") {
            ++unmapped
            if (note[id]=="") fail(id " is listed without a test and without a reason")
            for (test in passed) { base=test; sub(/\/.*/,"",base); if (base==id) fail(id " is listed as none but test " test " exists") }
            printf "%-8s none: %s\n",id,note[id]
            continue
        }
        n=split(listed[id],names,","); line=""
        for (k=1;k<=n;++k) {
            test=trim(names[k]); named[id "," test]=1
            if (!(test in passed)) fail(id " names test " test ", which did not run and pass")
            line=line (k>1?", ":"") test (test in changing?" (expected to change)":"")
        }
        printf "%-8s %s\n",id,line
    }
    for (test in passed) {
        base=test; sub(/\/.*/,"",base)
        if (base !~ /^C[AC]\./) continue
        if (!(base in required)) fail("test " test " names a row the spec does not have")
        else if (!((base "," test) in named)) fail("test " test " is missing from the coverage line of " base)
    }
    printf "rows: %d in the spec, %d with tests, %d listed without one\n",total,rows-unmapped,unmapped
    exit failed
}' "$spec" "$@"
