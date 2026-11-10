#ifndef PIPER_TEST_CHECK_H
#define PIPER_TEST_CHECK_H
/* The whole native test framework (docs/target-architecture.md, Q11): named
   tests, per-test check counts, a pass/fail summary and a non-zero exit.

   A test file lists its tests and hands them to check_main():

       static void lease_expiry(void) { ... CHECK(...); ... }
       static const struct check_test tests[]={
           {"CA.G2","lease expiry stops the lane",lease_expiry,NULL},
       };
       int main(void) { return check_main("rows-lane",tests,sizeof tests/sizeof *tests); }

   Each test prints one line, "ok <id> - <name> (<n> checks)" or
   "not ok <id> - <name>", and the file ends with a totals line. A failed
   CHECK ends only its own test; later tests still run, so one run reports
   every failing test. A test that characterizes a known bug or a planned
   change names it in `expected_to_change`; the line then carries
   "# expected to change: ...", and the pinned behaviour is not a golden one.

   IDs are stable. A test for one row of a section 3.1 transition table uses
   the row as its ID ("CA.G2", "CC.L11"; a second test of the same row adds
   "/variant"). tools-free coverage: tests/check_rows.sh reads these lines. */
#include <setjmp.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>

struct check_test {
    const char *id,*name;
    void (*run)(void);
    const char *expected_to_change;
};

/* One copy per test binary. The test's own file defines it; a helper that is
   linked into the binary shares it (CHECK_STATE_SHARED), so a CHECK failing
   inside the helper fails the running test like any other. */
#ifdef CHECK_STATE_SHARED
extern unsigned check_count;
extern jmp_buf check_recover;
extern bool check_running;
#else
unsigned check_count;
jmp_buf check_recover;
bool check_running;
#endif

static inline void check_fail(const char *file,int line,const char *expression)
{
    fprintf(stderr,"%s:%d: CHECK failed: %s\n",file,line,expression);
    /* Outside check_main() there is no test to fail, only the process. */
    if (!check_running) abort();
    longjmp(check_recover,1);
}
/* Always evaluates expr, even under -DNDEBUG, so side effects such as
   mkdtemp() or controller_init() can never be compiled out. */
#define CHECK(expr) do { ++check_count; if (!(expr)) check_fail(__FILE__,__LINE__,#expr); } while (0)

/* Its own function, so no local of check_main() lives across the setjmp. */
static inline bool check_run(const struct check_test *test)
{
    check_count=0; check_running=true;
    if (setjmp(check_recover)) { check_running=false; return false; }
    test->run();
    check_running=false;
    return true;
}
static inline int check_main(const char *suite,const struct check_test *tests,size_t count)
{
    unsigned failed=0,checks=0;
    for (size_t i=0;i<count;++i) {
        bool passed=check_run(&tests[i]); checks+=check_count;
        fflush(stderr);
        if (passed) printf("ok %s - %s (%u checks)",tests[i].id,tests[i].name,check_count);
        else { printf("not ok %s - %s",tests[i].id,tests[i].name); ++failed; }
        if (tests[i].expected_to_change) printf(" # expected to change: %s",tests[i].expected_to_change);
        putchar('\n'); fflush(stdout);
    }
    printf("%s %s: %u tests, %u passed, %u failed, %u checks\n",failed?"FAILED":"passed",suite,
        (unsigned)count,(unsigned)count-failed,failed,checks);
    return failed?1:0;
}
#endif
