using Xunit;

// MewUI 的 VisualTree.Visit 复用进程级静态栈，注释写明只在 UI 线程遍历。
// 测试没有 dispatcher：后台扫描回调里的 Dispatch 会直接在线程池线程执行，碰上另一个用例的树操作就会踩坏这个栈统计。
// xunit.runner.json 的 parallelizeTestCollections 只覆盖读取到该文件的适配器，这里用程序集特性兜底。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
