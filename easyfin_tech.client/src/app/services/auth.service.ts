import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, BehaviorSubject, of } from 'rxjs';
import { tap, catchError, map } from 'rxjs/operators';
import { Router } from '@angular/router';

export interface UserProfileDto {
  id?: string;
  userId?: string;
  fullName?: string;
  email: string;
  workspaceName?: string;
}

export interface ClientWorkspaceDto {
  id: string;
  name: string;
  businessName?: string;
}

export interface AuthResponseDto {
  success: boolean;
  message: string;
  user?: UserProfileDto;
  activeWorkspace?: ClientWorkspaceDto;
  workspaces?: ClientWorkspaceDto[];
}

export interface LoginRequest {
  email: string;
  password: string;
  rememberMe?: boolean;
}

export interface RegisterRequest {
  fullName: string;
  email: string;
  password: string;
  organization?: string;
}

@Injectable({
  providedIn: 'root'
})
export class AuthService {
  private readonly baseUrl = '/api/auth';
  private currentUserSubject = new BehaviorSubject<UserProfileDto | null>(null);
  public currentUser$ = this.currentUserSubject.asObservable();

  constructor(private http: HttpClient, private router: Router) {}

  get currentUser(): UserProfileDto | null {
    return this.currentUserSubject.value;
  }

  get isAuthenticated(): boolean {
    return !!this.currentUserSubject.value;
  }

  private extractUser(res: AuthResponseDto): UserProfileDto | null {
    if (!res || !res.user) return null;
    return {
      id: res.user.id || res.user.userId,
      userId: res.user.id || res.user.userId,
      email: res.user.email,
      fullName: res.user.fullName || res.activeWorkspace?.name || res.user.email.split('@')[0],
      workspaceName: res.activeWorkspace?.name || 'My Workspace'
    };
  }

  login(credentials: LoginRequest): Observable<AuthResponseDto> {
    return this.http.post<AuthResponseDto>(`${this.baseUrl}/login`, credentials, {
      withCredentials: true
    }).pipe(
      tap((res) => {
        if (res.success) {
          const user = this.extractUser(res);
          this.currentUserSubject.next(user);
        }
      })
    );
  }

  register(data: RegisterRequest): Observable<AuthResponseDto> {
    return this.http.post<AuthResponseDto>(`${this.baseUrl}/register`, data, {
      withCredentials: true
    }).pipe(
      tap((res) => {
        if (res.success) {
          const user = this.extractUser(res);
          this.currentUserSubject.next(user);
        }
      })
    );
  }

  logout(): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/logout`, {}, {
      withCredentials: true
    }).pipe(
      tap(() => {
        this.currentUserSubject.next(null);
        this.router.navigate(['/login']);
      }),
      catchError(() => {
        this.currentUserSubject.next(null);
        this.router.navigate(['/login']);
        return of(null);
      })
    );
  }

  checkAuth(): Observable<UserProfileDto | null> {
    return this.http.get<AuthResponseDto>(`${this.baseUrl}/me`, {
      withCredentials: true
    }).pipe(
      map((res) => {
        const user = this.extractUser(res);
        this.currentUserSubject.next(user);
        return user;
      }),
      catchError(() => {
        this.currentUserSubject.next(null);
        return of(null);
      })
    );
  }
}
