/*
 * Wallpapers from jonsolberg.se – Android app.
 * Copyright (C) 2026 Jon Solberg. Licensed under the GNU GPL v3 (see LICENSE).
 */
package se.jonsolberg.wallpapers;

import android.app.job.JobParameters;
import android.app.job.JobService;

/** The periodic background check (see Core.schedule). */
public class ChangeJob extends JobService {
    @Override
    public boolean onStartJob(JobParameters params) {
        new Thread(() -> {
            Core.runAutomatic(getApplicationContext());
            jobFinished(params, false);
        }).start();
        return true;
    }

    @Override
    public boolean onStopJob(JobParameters params) {
        return true;
    }
}
