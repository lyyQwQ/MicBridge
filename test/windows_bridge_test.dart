import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:micbridge/platform_bridge.dart';

Future<void> flushEvents() async {
  for (var i = 0; i < 8; i++) {
    await Future<void>.delayed(Duration.zero);
  }
}

class FakeSink implements IOSink {
  final completion = Completer<void>();
  void Function(String)? onWrite;
  bool throwWrite = false;
  bool throwClose = false;
  bool completeOnClose = true;
  bool closed = false;

  @override
  Future<void> get done => completion.future;
  @override
  void writeln([Object? object = '']) {
    if (throwWrite) throw StateError('sync write failed');
    onWrite?.call('$object');
  }

  @override
  Future<void> close() async {
    closed = true;
    if (throwClose) throw StateError('close failed');
    if (completeOnClose && !completion.isCompleted) completion.complete();
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class FakeProcess implements Process {
  final output = StreamController<List<int>>();
  final errors = StreamController<List<int>>();
  final exited = Completer<int>();
  final sink = FakeSink();
  final requests = <Map<String, dynamic>>[];
  bool killed = false;
  bool exitOnShutdown = true;
  bool completeOnKill = true;

  FakeProcess() {
    sink.onWrite = (line) {
      final request = jsonDecode(line) as Map<String, dynamic>;
      requests.add(request);
      if (request['command'] == 'shutdown') {
        reply(request);
        if (exitOnShutdown && !exited.isCompleted) exited.complete(0);
      }
    };
  }

  void reply(Map<String, dynamic> request) => output.add(
    utf8.encode(
      '${jsonEncode({
        'id': request['id'],
        'ok': true,
        'snapshot': {'state': 'idle'},
      })}\n',
    ),
  );

  @override
  IOSink get stdin => sink;
  @override
  Stream<List<int>> get stdout => output.stream;
  @override
  Stream<List<int>> get stderr => errors.stream;
  @override
  Future<int> get exitCode => exited.future;
  @override
  bool kill([ProcessSignal signal = ProcessSignal.sigterm]) {
    killed = true;
    if (completeOnKill && !exited.isCompleted) exited.complete(-1);
    return true;
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  test('dispose during startup closes and kills the late process', () async {
    final starting = Completer<Process>();
    final bridge = createWindowsMicPlatformForTesting(
      startProcess: (_, _) => starting.future,
    );
    final request = expectLater(bridge.status(), throwsStateError);
    await flushEvents();
    await bridge.dispose();
    final process = FakeProcess();
    starting.complete(process);
    await request;
    expect(process.killed, isTrue);
    expect(process.sink.closed, isTrue);
  });

  test('failed startup is retried by the next command', () async {
    final process = FakeProcess();
    var starts = 0;
    final bridge = createWindowsMicPlatformForTesting(
      startProcess: (_, _) async {
        if (++starts == 1) throw StateError('launch failed');
        return process;
      },
    );
    await expectLater(bridge.status(), throwsStateError);
    final next = bridge.status();
    await flushEvents();
    process.reply(process.requests.single);
    expect((await next).state, MicState.idle);
    expect(starts, 2);
    await bridge.dispose();
  });

  test('late old EOF cannot invalidate restarted process', () async {
    final old = FakeProcess();
    final current = FakeProcess();
    var starts = 0;
    final bridge = createWindowsMicPlatformForTesting(
      startProcess: (_, _) async => ++starts == 1 ? old : current,
    );
    final first = expectLater(bridge.status(), throwsStateError);
    await flushEvents();
    old.exited.complete(1);
    await first;
    final next = bridge.status();
    final checked = expectLater(next, completion(isA<MicSnapshot>()));
    await flushEvents();
    await old.output.close();
    // 旧管道可以在新进程启动后才完成。
    if (!old.sink.completion.isCompleted) old.sink.completion.complete();
    await flushEvents();
    current.reply(current.requests.single);
    await checked;
    expect(starts, 2);
    await bridge.dispose();
  });

  test(
    'late exit and stdin error after pipe failure leave new process alive',
    () async {
      final old = FakeProcess()..completeOnKill = false;
      old.sink.completeOnClose = false;
      final current = FakeProcess();
      var starts = 0;
      final bridge = createWindowsMicPlatformForTesting(
        startProcess: (_, _) async => ++starts == 1 ? old : current,
      );
      final first = expectLater(bridge.status(), throwsStateError);
      await flushEvents();
      old.output.addError(StateError('broken stdout'));
      await first;
      final next = bridge.status();
      final checked = expectLater(next, completion(isA<MicSnapshot>()));
      await flushEvents();
      old.exited.complete(1);
      old.sink.completion.completeError(StateError('late stdin failure'));
      await flushEvents();
      current.reply(current.requests.single);
      await checked;
      expect(current.killed, isFalse);
      await bridge.dispose();
    },
  );

  test('synchronous startup exception can also be retried', () async {
    final process = FakeProcess();
    var starts = 0;
    final bridge = createWindowsMicPlatformForTesting(
      startProcess: (_, _) {
        if (++starts == 1) throw StateError('synchronous launch failure');
        return Future.value(process);
      },
    );
    await expectLater(bridge.status(), throwsStateError);
    final next = bridge.status();
    await flushEvents();
    process.reply(process.requests.single);
    expect((await next).state, MicState.idle);
    expect(starts, 2);
    await bridge.dispose();
  });

  test('asynchronous stdin error fails request immediately once', () async {
    final process = FakeProcess();
    final bridge = createWindowsMicPlatformForTesting(
      startProcess: (_, _) async => process,
    );
    var failures = 0;
    final request = bridge.status().then<void>(
      (_) => fail('unexpected success'),
      onError: (Object error) {
        failures++;
        expect(error, isStateError);
      },
    );
    await flushEvents();
    process.sink.completion.completeError(StateError('async write failed'));
    await request.timeout(const Duration(seconds: 1));
    process.output.addError(StateError('second pipe error'));
    await flushEvents();
    expect(failures, 1);
    await bridge.dispose();
  });

  test(
    'synchronous stdin error fails request once without unhandled future',
    () async {
      final process = FakeProcess()..sink.throwWrite = true;
      final bridge = createWindowsMicPlatformForTesting(
        startProcess: (_, _) async => process,
      );
      var failures = 0;
      await bridge.status().then<void>(
        (_) => fail('unexpected success'),
        onError: (Object error) {
          failures++;
          expect(error, isStateError);
        },
      );
      await flushEvents();
      expect(failures, 1);
      await bridge.dispose();
    },
  );

  test(
    'stdout EOF fails all pending requests without request timeout',
    () async {
      final process = FakeProcess();
      final bridge = createWindowsMicPlatformForTesting(
        startProcess: (_, _) async => process,
      );
      final first = expectLater(
        bridge.status().timeout(const Duration(seconds: 1)),
        throwsStateError,
      );
      final second = expectLater(
        bridge.start().timeout(const Duration(seconds: 1)),
        throwsStateError,
      );
      await flushEvents();
      await process.output.close();
      await Future.wait([first, second]);
      await bridge.dispose();
    },
  );

  test('dispose still kills when stdin close throws', () async {
    final process = FakeProcess()..exitOnShutdown = false;
    process.sink.throwClose = true;
    final bridge = createWindowsMicPlatformForTesting(
      startProcess: (_, _) async => process,
    );
    final status = bridge.status();
    await flushEvents();
    process.reply(process.requests.single);
    await status;
    await bridge.dispose();
    expect(process.requests.last['command'], 'shutdown');
    expect(process.sink.closed, isTrue);
    expect(process.killed, isTrue);
  });

  test('chunked UTF8 JSON with no trailing newline is parsed at EOF', () async {
    final process = FakeProcess();
    final bridge = createWindowsMicPlatformForTesting(
      startProcess: (_, _) async => process,
    );
    final status = bridge.status();
    await flushEvents();
    final bytes = utf8.encode(
      jsonEncode({
        'id': process.requests.single['id'],
        'ok': true,
        'snapshot': {'state': 'idle', 'detail': '眼镜'},
      }),
    );
    for (final byte in bytes) {
      process.output.add([byte]);
    }
    await process.output.close();
    expect((await status).detail, '眼镜');
    await bridge.dispose();
  });
}
