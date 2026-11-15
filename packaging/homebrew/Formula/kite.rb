class Kite < Formula
  desc "High-throughput .NET 10 edge web server, reverse proxy, TLS/ACME gateway, and polyglot app host"
  homepage "https://github.com/kite-construction-team/kite"
  version "1.0.0"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/kite-construction-team/kite/releases/download/v1.0.0/kite-v1.0.0-osx-arm64.tar.gz"
      sha256 "0000000000000000000000000000000000000000000000000000000000000000"
    end

    on_intel do
      url "https://github.com/kite-construction-team/kite/releases/download/v1.0.0/kite-v1.0.0-osx-x64.tar.gz"
      sha256 "0000000000000000000000000000000000000000000000000000000000000000"
    end
  end

  def install
    libexec.install Dir["*"]

    %w[kite Kite.Server Kite.Worker kite-server].each do |binary|
      target = libexec/binary
      chmod 0755, target if target.exist?
    end

    bin.write_exec_script libexec/"kite"
    bin.write_exec_script libexec/"kite-server"
    bin.write_exec_script libexec/"Kite.Server"
  end

  def post_install
    (var/"kite").mkpath
    (etc/"kite").mkpath
    (var/"log").mkpath
  end

  def caveats
    <<~EOS
      Kite CLI and server binaries are installed as:
        kite
        kite-server (Kite.Server)

      Default user data directory:
        ~/Library/Application Support/Kite/kite-data (override with --home or KITE_HOME)

      Homebrew background service data directory:
        #{var}/kite

      Start as a background macOS service:
        brew services start kite

      Inspect resolved paths and configuration:
        kite paths
    EOS
  end

  service do
    run [opt_bin/"kite", "run", "--home", var/"kite"]
    keep_alive true
    working_dir var/"kite"
    log_path var/"log/kite.log"
    error_log_path var/"log/kite-error.log"
  end

  test do
    assert_match "kite #{version}", shell_output("#{bin}/kite --version")
    assert_match "kite-server #{version}", shell_output("#{bin}/kite-server --version")
    system bin/"kite", "init", "--home", testpath/"kite-home"
    system bin/"kite", "validate", "--home", testpath/"kite-home"
  end
end
