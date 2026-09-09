import { Component, OnInit, ChangeDetectorRef } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { StatementService } from '../../services/statement.service';
import { AuthService } from '../../services/auth.service';

export interface DashboardRecentStatement {
  id: string;
  originalFileName: string;
  bankName?: string;
  uploadedAt: string;
  status: string;
  processingStatus: number;
  transactionCount: number;
  fileSizeBytes: number;
  fileSizeFormatted: string;
}

export interface DashboardSummary {
  status: string;
  database: string;
  server: string;
  canConnect: boolean;
  totalStatements: number;
  completedStatements: number;
  statementsProcessed: number;
  processingStatements: number;
  failedStatements: number;
  totalTransactions: number;
  clientProfiles: number;
  recentStatements: DashboardRecentStatement[];
}

@Component({
  selector: 'app-dashboard',
  templateUrl: './dashboard.component.html',
  styleUrls: ['./dashboard.component.css'],
  standalone: false
})
export class DashboardComponent implements OnInit {
  stats: DashboardSummary = {
    status: 'Online / Connected',
    database: 'EasyFin_Tech',
    server: 'localhost\\SQLEXPRESS',
    canConnect: true,
    totalStatements: 0,
    completedStatements: 0,
    statementsProcessed: 0,
    processingStatements: 0,
    failedStatements: 0,
    totalTransactions: 0,
    clientProfiles: 1,
    recentStatements: []
  };

  loading = true;
  errorMessage: string | null = null;
  userWorkspaceName = 'Connected Workspace';

  constructor(
    private http: HttpClient,
    public statementService: StatementService,
    private authService: AuthService,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.loadDashboardData();
  }

  loadDashboardData(): void {
    this.loading = true;
    this.errorMessage = null;

    if (this.authService.currentUser?.workspaceName) {
      this.userWorkspaceName = this.authService.currentUser.workspaceName;
    }

    this.http.get<DashboardSummary>('/api/dashboard/summary').subscribe({
      next: (summary) => {
        if (summary) {
          this.stats = {
            ...summary,
            recentStatements: summary.recentStatements || []
          };
        }
        this.loading = false;
        this.cdr.markForCheck();
      },
      error: (err) => {
        console.error('Failed to load dashboard summary:', err);
        this.errorMessage = 'Unable to connect to the server or load dashboard statistics. Please try again.';
        this.loading = false;
        this.cdr.markForCheck();
      }
    });
  }

  downloadStatement(item: DashboardRecentStatement): void {
    this.statementService.downloadStatementFile(item.id, item.originalFileName);
  }
}
