// Each test spins up its own loopback UDP sockets, so classes are independent and can run in
// parallel. Methods within a class are kept sequential to keep socket use predictable.
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]
